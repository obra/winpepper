namespace Winpepper.Asr.TranscribeCpp.Worker;

/// <summary>
/// Client-side ITranscribeCppEngine over a worker subprocess. Keeps the
/// existing engine seam so NemotronStreamingTranscriber, StreamingRouteGuard,
/// and their tests are untouched; what changes is that a wedged native call
/// is now KILLABLE (RPC deadline -> Kill) and the engine RESTARTABLE (lazy
/// respawn on the next call, bounded by WorkerRestartPolicy).
///
/// Failure contract: any RPC failure kills the worker, invalidates open
/// stream proxies, and throws TranscribeCppException — the exact exception
/// the in-process engine used, so every existing fallback path just works.
/// Two deliberate exceptions: Dispose() latches the engine DEAD (later calls
/// throw ObjectDisposedException and never respawn — a retained reference
/// across a model swap must not resurrect an old-layout worker), and the
/// oversize batch pre-check throws InvalidOperationException WITHOUT touching
/// the worker (the ladder's FallbackTranscriber routes it to Parakeet).
/// </summary>
public sealed class WorkerProcessEngine : ITranscribeCppEngine
{
    private readonly IWorkerProcessFactory _factory;
    private readonly string _runtimeDir;
    private readonly string _ggufPath;
    private readonly WorkerEngineOptions _options;
    private readonly WorkerRestartPolicy _restartPolicy;
    private readonly Action<string>? _log;
    private readonly object _rpcGate = new();

    private IWorkerProcess? _proc;
    private int _generation; // bumped on every kill; stream proxies check it
    private bool _disposed;  // set by Dispose(); a disposed engine never respawns (V5/A10)

    public WorkerProcessEngine(IWorkerProcessFactory factory, string runtimeDir, string ggufPath,
        string modelName, WorkerEngineOptions? options = null,
        WorkerRestartPolicy? restartPolicy = null, Action<string>? log = null)
    {
        _factory = factory;
        _runtimeDir = runtimeDir;
        _ggufPath = ggufPath;
        ModelName = modelName;
        _options = options ?? new WorkerEngineOptions();
        _restartPolicy = restartPolicy ?? new WorkerRestartPolicy();
        _log = log;
    }

    public string ModelName { get; }

    public ITranscribeCppStream BeginStream(int attContextRight, string? language, out int gateWaitMs)
    {
        lock (_rpcGate)
        {
            EnsureWorkerLocked();
            var payload = Build(w => { w.Write(attContextRight); WorkerWire.WriteString(w, language); });
            var (op, response) = RpcLocked(WorkerOp.BeginStream, payload, _options.BeginStreamTimeout);
            using var r = Reader(response);
            if (op == WorkerOp.Error) throw ReadError(r, out gateWaitMs);
            gateWaitMs = r.ReadInt32();
            return new WorkerStream(this, _generation);
        }
    }

    public string TranscribeBatch(float[] mono16k, string? language, out int gateWaitMs)
    {
        // Oversize pre-check BEFORE any RPC or spawn: a frame above the wire
        // cap would kill the worker (fatal InvalidDataException in its reader).
        // Throwing InvalidOperationException (not TranscribeCppException) here
        // lets the ladder's FallbackTranscriber route to Parakeet when installed.
        if ((long)mono16k.Length * sizeof(float) + 64 > WorkerWire.MaxPayloadBytes)
            throw new InvalidOperationException(
                "dictation too long for the local batch engine (> ~17 minutes); shorten the recording");
        lock (_rpcGate)
        {
            EnsureWorkerLocked();
            var payload = Build(w => { WorkerWire.WriteString(w, language); WorkerWire.WriteFloats(w, mono16k, mono16k.Length); });
            // Length-aware deadline: BatchTimeout is a FLOOR. A cap-sized batch
            // measured ~106 s on the dev host vs the fixed 120 s (1.13x headroom);
            // 2 s per audio-second covers worst-case RTF~2 low-end hardware.
            var batchDeadline = TimeSpan.FromSeconds(Math.Max(
                _options.BatchTimeout.TotalSeconds, 30 + 2.0 * (mono16k.Length / 16000.0)));
            var (op, response) = RpcLocked(WorkerOp.TranscribeBatch, payload, batchDeadline);
            using var r = Reader(response);
            if (op == WorkerOp.Error) throw ReadError(r, out gateWaitMs);
            gateWaitMs = r.ReadInt32();
            var text = WorkerWire.ReadString(r) ?? "";
            // A completed dictation is the only success credit: it proves the
            // kill->respawn cycle actually recovered (council fix #1).
            _restartPolicy.NoteSuccess();
            return text;
        }
    }

    public void Dispose()
    {
        lock (_rpcGate)
        {
            if (_disposed) return;
            _disposed = true; // latch: EnsureWorkerLocked refuses to respawn from now on
            if (_proc is { HasExited: false })
            {
                try { RpcLocked(WorkerOp.Shutdown, Array.Empty<byte>(), _options.DisposeTimeout); }
                catch { /* shutdown is best-effort; Kill below is the guarantee */ }
            }
            KillLocked("dispose");
        }
    }

    // ---- internals -------------------------------------------------------

    private void EnsureWorkerLocked()
    {
        // A disposed engine must stay dead: without this latch a retained
        // reference (e.g. a live dictation captured across a model swap)
        // would silently respawn a worker for the OLD layout (V5/A10).
        if (_disposed) throw new ObjectDisposedException(nameof(WorkerProcessEngine));
        if (_proc is { HasExited: false }) return;
        if (!_restartPolicy.CanAttempt())
        {
            _log?.Invoke("speech worker restart blocked by budget; next attempt after cooldown");
            throw new TranscribeCppException("speech worker restart budget exhausted; retrying after cooldown");
        }
        var spawned = false;
        try
        {
            _proc = _factory.Start();
            spawned = true;
            _log?.Invoke("speech worker started");
            var payload = Build(w => { WorkerWire.WriteString(w, _runtimeDir); WorkerWire.WriteString(w, _ggufPath); });
            var (op, response) = RpcLocked(WorkerOp.Load, payload, _options.LoadTimeout);
            using var r = Reader(response);
            if (op == WorkerOp.Error) throw ReadError(r, out _);
            var loadedName = WorkerWire.ReadString(r);
            // Success is credited ONLY by a completed operation RPC (batch
            // returned text / stream finalized) — never by Load. Crediting
            // Load made the budget oscillate 0<->1 across every
            // kill->respawn->Load cycle, so it could never bound the
            // operation-phase kills it exists to bound (council fix #1).
            _log?.Invoke($"speech worker load ok ({loadedName})");
        }
        catch (Exception e)
        {
            // Failures inside RpcLocked (timeout / broken pipe) already
            // noted themselves and killed the worker, nulling _proc. The two
            // cases still uncounted here: _factory.Start() threw
            // (spawned == false), and Load answered with an Error frame
            // (worker alive, _proc != null).
            var alreadyCounted = spawned && _proc is null;
            if (!alreadyCounted) _restartPolicy.NoteFailure();
            KillLocked($"load failed: {e.Message}");
            throw e as TranscribeCppException
                  ?? new TranscribeCppException($"speech worker failed to start: {e.Message}");
        }
    }

    /// <summary>One request -> one response, bounded by a deadline. On ANY
    /// failure the worker is killed (a connection that missed a deadline can
    /// never be reused: a late response would answer the wrong request).</summary>
    private (WorkerOp Op, byte[] Payload) RpcLocked(WorkerOp op, byte[] payload, TimeSpan timeout)
    {
        var proc = _proc ?? throw new TranscribeCppException("speech worker is not running");
        try
        {
            WorkerWire.WriteFrame(proc.Input, op, payload);
            var read = Task.Run(() => WorkerWire.ReadFrame(proc.Output));
            if (!read.Wait(timeout))
            {
                _restartPolicy.NoteFailure(); // operation-phase kills charge the budget (council fix #1)
                KillLocked($"{op} timed out after {(int)timeout.TotalMilliseconds} ms");
                // The abandoned reader faults once the killed worker's pipe closes;
                // observe it so it can never surface as TaskScheduler.UnobservedTaskException
                // (which would fire the app's crash machinery at an arbitrary later moment).
                read.ContinueWith(t => _ = t.Exception,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                throw new TranscribeCppException(
                    $"speech worker did not respond to {op} within {(int)timeout.TotalSeconds} s; worker killed and will restart on the next call");
            }
            return read.Result;
        }
        catch (TranscribeCppException) { throw; }
        catch (Exception e)
        {
            var inner = (e as AggregateException)?.InnerException ?? e;
            _restartPolicy.NoteFailure(); // connection failures charge the budget too (council fix #1)
            KillLocked($"{op} failed: {inner.Message}");
            throw new TranscribeCppException($"speech worker connection failed during {op}: {inner.Message}");
        }
    }

    private void KillLocked(string reason)
    {
        if (_proc is null) return;
        _log?.Invoke($"speech worker killed: {reason}");
        _generation++;
        try { _proc.Kill(); } catch { /* already dead */ }
        try { _proc.Dispose(); } catch { /* already dead */ }
        _proc = null;
    }

    private static byte[] Build(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) write(w);
        return ms.ToArray();
    }

    private static BinaryReader Reader(byte[] payload)
        => new(new MemoryStream(payload), System.Text.Encoding.UTF8);

    private static TranscribeCppException ReadError(BinaryReader r, out int gateWaitMs)
    {
        gateWaitMs = r.ReadInt32();
        var type = WorkerWire.ReadString(r);
        var message = WorkerWire.ReadString(r) ?? "unknown worker error";
        return type == nameof(TranscribeCppException)
            ? new TranscribeCppException(message)
            : new TranscribeCppException($"{type}: {message}");
    }

    /// <summary>Per-dictation stream proxy. Bound to the worker generation it
    /// was created under: after a kill/respawn it throws on use (the stream
    /// state died with the worker) and disposes as a no-op.</summary>
    private sealed class WorkerStream : ITranscribeCppStream
    {
        private readonly WorkerProcessEngine _owner;
        private readonly int _generation;
        private bool _disposed;

        internal WorkerStream(WorkerProcessEngine owner, int generation)
        {
            _owner = owner;
            _generation = generation;
        }

        public string? Feed(float[] samples, int count)
        {
            lock (_owner._rpcGate)
            {
                ThrowIfLostLocked();
                var payload = Build(w => WorkerWire.WriteFloats(w, samples, count));
                var (op, response) = _owner.RpcLocked(WorkerOp.Feed, payload, _owner._options.FeedTimeout);
                using var r = Reader(response);
                if (op == WorkerOp.Error) throw ReadError(r, out _);
                return WorkerWire.ReadString(r);
            }
        }

        public (string Text, bool WasTruncated) Finalize()
        {
            lock (_owner._rpcGate)
            {
                ThrowIfLostLocked();
                var (op, response) = _owner.RpcLocked(WorkerOp.FinalizeStream, Array.Empty<byte>(), _owner._options.FinalizeTimeout);
                using var r = Reader(response);
                if (op == WorkerOp.Error) throw ReadError(r, out _);
                var text = WorkerWire.ReadString(r) ?? "";
                var truncated = r.ReadBoolean();
                _owner._restartPolicy.NoteSuccess(); // a finished stream dictation resets the budget
                return (text, truncated);
            }
        }

        public void Dispose()
        {
            lock (_owner._rpcGate)
            {
                if (_disposed) return;
                _disposed = true;
                if (_generation != _owner._generation || _owner._proc is not { HasExited: false })
                    return; // the stream died with its worker; nothing to release
                try { _owner.RpcLocked(WorkerOp.DisposeStream, Array.Empty<byte>(), _owner._options.DisposeTimeout); }
                catch { /* a failed dispose already killed the worker, which also frees the gate */ }
            }
        }

        private void ThrowIfLostLocked()
        {
            if (_disposed) throw new TranscribeCppException("stream already disposed");
            if (_generation != _owner._generation || _owner._proc is not { HasExited: false })
                throw new TranscribeCppException("stream lost: the speech worker was restarted");
        }
    }
}
