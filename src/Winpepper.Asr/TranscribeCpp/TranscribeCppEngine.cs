using System.Runtime.InteropServices;

namespace Winpepper.Asr.TranscribeCpp;

/// <summary>
/// Real transcribe.cpp engine. Load() performs, in order:
///  1. contract.json gate (exact version 0.1.3 + header_hash 86b16dd97ad1cb58)
///     — BEFORE any native library is loaded;
///  2. DllImportResolver registration mapping "transcribe" to
///     &lt;runtimeDir&gt;\transcribe.dll (process-wide, first runtimeDir wins);
///  3. log callback install (static delegate, process lifetime);
///  4. native version string gate (must equal 0.1.3);
///  5. ABI struct-size gate for all 6 marshaled structs (non-short-circuit,
///     all mismatches reported);
///  6. transcribe_init_backends(runtimeDir) — REQUIRED before model load
///     (GGML_BACKEND_DL build; ggml-*.dll live in runtimeDir);
///  7. model load with backend = CPU (Vulkan measured unusable: ~16 s warm-up);
///  8. capabilities gate: supports_streaming, native_sample_rate == 16000,
///     PKST ext accepted.
/// Any failure throws TranscribeCppException — callers fall back to batch.
/// The model handle lives until Dispose (in practice: process lifetime via
/// NemotronEngineHolder). Each BeginStream/TranscribeBatch uses its own native
/// session. A stream/session is single-threaded, AND — v0.1.3 header contract
/// (transcribe.h:11-20) — at most ONE compute (run or active stream) may be in
/// flight across ALL sessions of a model: _computeGate serializes them.
/// BeginStream holds the gate for the stream's lifetime (released on stream
/// dispose); TranscribeBatch holds it per call. A 5 s acquire timeout turns a
/// stuck predecessor into a TranscribeCppException (=> batch fallback), never
/// a deadlock.
/// Load(batchOnly: true) skips only the streaming capability checks (validated:
/// v0.1.3's qwen3_asr family reports no streaming capability and its
/// .accepts_ext_kind is null, so the unmodified gate refuses a model that batch
/// transcribe_run fully supports); BeginStream then throws loud.
/// </summary>
public sealed class TranscribeCppEngine : ITranscribeCppEngine
{
    private static readonly object s_processInit = new();
    private static bool s_resolverInstalled;
    private static string? s_runtimeDir;
    private static Action<string>? s_logWarning;
    // Keep the delegate alive for process lifetime — native holds the pointer.
    private static readonly TranscribeCppNative.LogCallback s_log = (level, msg, _) =>
    {
        if (level == 2 /*WARN*/ || level == 3 /*ERROR*/)
            s_logWarning?.Invoke($"[transcribe.cpp:{(level == 3 ? "ERROR" : "WARN")}] {TranscribeCppNative.Str(msg)}");
    };

    private readonly IntPtr _model;
    // v0.1.3 contract: at most one compute in flight per model across ALL
    // sessions (transcribe.h:11-20). BeginStream holds this for the stream's
    // lifetime; TranscribeBatch per call.
    private readonly SemaphoreSlim _computeGate = new(1, 1);
    private static readonly TimeSpan s_gateTimeout = TimeSpan.FromSeconds(5);
    private readonly bool _batchOnly;
    private bool _disposed;

    public string ModelName { get; }

    private TranscribeCppEngine(IntPtr model, string modelName, bool batchOnly)
    {
        _model = model;
        ModelName = modelName;
        _batchOnly = batchOnly;
    }

    public static TranscribeCppEngine Load(string runtimeDir, string modelPath, Action<string>? logWarning = null, bool batchOnly = false)
    {
        // 1. contract gate — pure file IO, safe on any OS, BEFORE LoadLibrary.
        var contractPath = Path.Combine(runtimeDir, "contract.json");
        if (!File.Exists(contractPath))
            throw new TranscribeCppException($"contract.json not found in runtime dir: {runtimeDir}");
        var contract = TranscribeCppContract.Load(contractPath);
        if (!contract.IsCompatible)
            throw new TranscribeCppException(
                $"transcribe.cpp runtime contract mismatch: found version={contract.Version} " +
                $"header_hash={contract.HeaderHash}, require version={TranscribeCppContract.RequiredVersion} " +
                $"header_hash={TranscribeCppContract.RequiredHeaderHash}. Refusing to load.");

        if (!OperatingSystem.IsWindows())
            throw new TranscribeCppException("transcribe.cpp engine is Windows-only in winpepper");
        if (IntPtr.Size != 8)
            throw new TranscribeCppException("transcribe.cpp binding requires a 64-bit process");

        lock (s_processInit)
        {
            if (!s_resolverInstalled)
            {
                s_runtimeDir = runtimeDir;
                s_logWarning = logWarning;
                NativeLibrary.SetDllImportResolver(typeof(TranscribeCppEngine).Assembly,
                    (name, _, _) => name == "transcribe"
                        ? NativeLibrary.Load(Path.Combine(s_runtimeDir!, "transcribe.dll"))
                        : IntPtr.Zero);
                s_resolverInstalled = true;
                // 3. first native call, per header contract: once, at startup.
                // transcribe.dll statically imports the VC++ 2015-2022 x64 CRT
                // (msvcp140/vcruntime140/vcruntime140_1 — verified by PE import
                // dump); on a machine without the redist the first call throws
                // DllNotFoundException. Name the fix in the error.
                try
                {
                    TranscribeCppNative.transcribe_log_set(s_log, IntPtr.Zero);
                }
                catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
                {
                    throw new TranscribeCppException(
                        "failed to load transcribe.dll — likely missing the Microsoft " +
                        "Visual C++ 2015-2022 x64 Redistributable (msvcp140/vcruntime140). " +
                        "Install it from aka.ms/vs/17/release/vc_redist.x64.exe and retry.", e);
                }
            }
            else if (!string.Equals(s_runtimeDir, runtimeDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new TranscribeCppException(
                    $"transcribe.cpp already initialized from '{s_runtimeDir}'; cannot re-init from '{runtimeDir}' (restart required)");
            }

            // 4. version gate
            var ver = TranscribeCppNative.Str(TranscribeCppNative.transcribe_version());
            if (ver != TranscribeCppContract.RequiredVersion)
                throw new TranscribeCppException($"native transcribe version is {ver}, require {TranscribeCppContract.RequiredVersion}");

            // 5. ABI gate — every marshaled struct, all reported (& not &&).
            var mismatches = new List<string>();
            void Abi(int id, int managed, string name)
            {
                var native = TranscribeCppNative.transcribe_abi_struct_size(id).ToUInt64();
                if (native != (ulong)managed) mismatches.Add($"{name}: native={native} managed={managed}");
            }
            Abi(TranscribeCppNative.ABI_MODEL_LOAD_PARAMS, Marshal.SizeOf<TranscribeCppNative.ModelLoadParams>(), "model_load_params");
            Abi(TranscribeCppNative.ABI_STREAM_PARAMS, Marshal.SizeOf<TranscribeCppNative.StreamParams>(), "stream_params");
            Abi(TranscribeCppNative.ABI_CAPABILITIES, Marshal.SizeOf<TranscribeCppNative.Capabilities>(), "capabilities");
            Abi(TranscribeCppNative.ABI_STREAM_UPDATE, Marshal.SizeOf<TranscribeCppNative.StreamUpdate>(), "stream_update");
            Abi(TranscribeCppNative.ABI_STREAM_TEXT, Marshal.SizeOf<TranscribeCppNative.StreamText>(), "stream_text");
            Abi(TranscribeCppNative.ABI_RUN_PARAMS, Marshal.SizeOf<TranscribeCppNative.RunParams>(), "run_params");
            if (mismatches.Count > 0)
                throw new TranscribeCppException("ABI struct size mismatch: " + string.Join("; ", mismatches));

            // 6. dynamic ggml backends live beside transcribe.dll
            var st = TranscribeCppNative.transcribe_init_backends(runtimeDir);
            if (st != 0)
                throw new TranscribeCppException($"transcribe_init_backends failed: {TranscribeCppNative.Status(st)}");
        }

        // 7. model load — CPU backend only (see plan: Vulkan warm-up unusable).
        var p = new TranscribeCppNative.ModelLoadParams();
        TranscribeCppNative.transcribe_model_load_params_init(ref p);
        p.backend = TranscribeCppNative.BACKEND_CPU;
        var stLoad = TranscribeCppNative.transcribe_model_load_file(modelPath, ref p, out var model);
        if (stLoad != 0)
            throw new TranscribeCppException($"model load failed ({modelPath}): {TranscribeCppNative.Status(stLoad)}");

        try
        {
            // 8. capability gates
            var caps = new TranscribeCppNative.Capabilities();
            TranscribeCppNative.transcribe_capabilities_init(ref caps);
            var stCaps = TranscribeCppNative.transcribe_model_get_capabilities(model, ref caps);
            if (stCaps != 0)
                throw new TranscribeCppException($"get_capabilities failed: {TranscribeCppNative.Status(stCaps)}");
            if (!batchOnly && caps.supports_streaming == 0)
                throw new TranscribeCppException("model does not support streaming");
            if (caps.native_sample_rate != 16000)
                throw new TranscribeCppException($"model native_sample_rate is {caps.native_sample_rate}, require 16000");
            if (!batchOnly && !TranscribeCppNative.transcribe_model_accepts_ext_kind(
                    model, TranscribeCppNative.EXT_SLOT_STREAM, TranscribeCppNative.EXT_KIND_PARAKEET_STREAM))
                throw new TranscribeCppException("model rejects the PKST stream extension");

            return new TranscribeCppEngine(model, NemotronStreamingModel.Name, batchOnly);
        }
        catch
        {
            TranscribeCppNative.transcribe_model_free(model);
            throw;
        }
    }

    /// <summary>Marshals a transcribe_run_params carrying a language hint. Returns
    /// (Zero, Zero) for a null language so callers pass IntPtr.Zero exactly as before.
    /// Free with FreeRunParams immediately after the native call returns — the header
    /// guarantees run params and their strings are copied before transcribe_run /
    /// transcribe_stream_begin return.</summary>
    private static (IntPtr Params, IntPtr Lang) AllocRunParams(string? language)
    {
        if (language is null) return (IntPtr.Zero, IntPtr.Zero);
        var rp = new TranscribeCppNative.RunParams();
        TranscribeCppNative.transcribe_run_params_init(ref rp);
        var pLang = Marshal.StringToCoTaskMemUTF8(language);
        rp.language = pLang;
        var pRp = Marshal.AllocHGlobal(Marshal.SizeOf<TranscribeCppNative.RunParams>());
        Marshal.StructureToPtr(rp, pRp, fDeleteOld: false);
        return (pRp, pLang);
    }

    private static void FreeRunParams((IntPtr Params, IntPtr Lang) rp)
    {
        if (rp.Params != IntPtr.Zero) Marshal.FreeHGlobal(rp.Params);
        if (rp.Lang != IntPtr.Zero) Marshal.FreeCoTaskMem(rp.Lang);
    }

    public ITranscribeCppStream BeginStream(int attContextRight, string? language, out int gateWaitMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_batchOnly)
            throw new InvalidOperationException("engine was loaded batch-only; streaming is unavailable");
        // One compute in flight per model: hold the gate for the stream's
        // lifetime. A previous dictation's stream normally disposes in ms; a
        // 5 s timeout means a stuck one degrades to batch, never corrupts.
        var gateSw = System.Diagnostics.Stopwatch.StartNew();
        var acquired = _computeGate.Wait(s_gateTimeout);
        gateSw.Stop();
        gateWaitMs = (int)gateSw.ElapsedMilliseconds; // per-call, valid even on the throw below
        if (!acquired)
            throw new TranscribeCppException(
                "another transcription is still active on the engine (compute gate timeout)");
        try
        {
            return BeginStreamHoldingGate(attContextRight, language);
        }
        catch
        {
            _computeGate.Release();
            throw;
        }
    }

    private ITranscribeCppStream BeginStreamHoldingGate(int attContextRight, string? language)
    {
        var st = TranscribeCppNative.transcribe_session_init(_model, IntPtr.Zero, out var session);
        if (st != 0)
            throw new TranscribeCppException($"session_init failed: {TranscribeCppNative.Status(st)}");
        try
        {
            // PKST ext + stream params must be heap memory: stream_begin reads
            // raw pointers. begin copies everything out — free immediately after.
            var ext = new TranscribeCppNative.ParakeetStreamExt();
            TranscribeCppNative.transcribe_parakeet_stream_ext_init(ref ext);
            ext.att_context_right = attContextRight;
            var pExt = Marshal.AllocHGlobal(Marshal.SizeOf<TranscribeCppNative.ParakeetStreamExt>());
            var pSp = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(ext, pExt, false);
                var sp = new TranscribeCppNative.StreamParams();
                TranscribeCppNative.transcribe_stream_params_init(ref sp);
                sp.family = pExt;
                pSp = Marshal.AllocHGlobal(Marshal.SizeOf<TranscribeCppNative.StreamParams>());
                Marshal.StructureToPtr(sp, pSp, false);
                var runParams = AllocRunParams(language);
                int stBegin;
                try { stBegin = TranscribeCppNative.transcribe_stream_begin(session, runParams.Params, pSp); }
                finally { FreeRunParams(runParams); }
                if (stBegin != 0)
                    throw new TranscribeCppException($"stream_begin failed: {TranscribeCppNative.Status(stBegin)}");
            }
            finally
            {
                Marshal.FreeHGlobal(pExt);
                if (pSp != IntPtr.Zero) Marshal.FreeHGlobal(pSp);
            }
            return new NativeStream(session, () => _computeGate.Release());
        }
        catch
        {
            TranscribeCppNative.transcribe_session_free(session);
            throw;
        }
    }

    public string TranscribeBatch(float[] mono16k, string? language, out int gateWaitMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        gateWaitMs = 0; // definite assignment for the empty-input early return
        if (mono16k.Length == 0) return "";
        var gateSw = System.Diagnostics.Stopwatch.StartNew();
        var acquired = _computeGate.Wait(s_gateTimeout);
        gateSw.Stop();
        gateWaitMs = (int)gateSw.ElapsedMilliseconds; // per-call, valid even on the throw below
        if (!acquired)
            throw new TranscribeCppException(
                "another transcription is still active on the engine (compute gate timeout)");
        try
        {
            var st = TranscribeCppNative.transcribe_session_init(_model, IntPtr.Zero, out var session);
            if (st != 0)
                throw new TranscribeCppException($"session_init failed: {TranscribeCppNative.Status(st)}");
            try
            {
                var runParams = AllocRunParams(language);
                int stRun;
                try { stRun = TranscribeCppNative.transcribe_run(session, mono16k, mono16k.Length, runParams.Params); }
                finally { FreeRunParams(runParams); }
                if (stRun != 0)
                    throw new TranscribeCppException($"transcribe_run failed: {TranscribeCppNative.Status(stRun)}");
                // Copy immediately — pointer dies with the session.
                return TranscribeCppNative.Str(TranscribeCppNative.transcribe_full_text(session));
            }
            finally
            {
                TranscribeCppNative.transcribe_session_free(session);
            }
        }
        finally
        {
            _computeGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TranscribeCppNative.transcribe_model_free(_model);
    }

    private sealed class NativeStream : ITranscribeCppStream
    {
        private readonly IntPtr _session;
        private readonly Action _releaseComputeGate;
        private string _lastCommitted = "";
        private bool _disposed;

        public NativeStream(IntPtr session, Action releaseComputeGate)
        {
            _session = session;
            _releaseComputeGate = releaseComputeGate;
        }

        public string? Feed(float[] samples, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (count <= 0) return null;
            var chunk = samples;
            if (count != samples.Length)
            {
                chunk = new float[count];
                Array.Copy(samples, chunk, count);
            }
            var upd = new TranscribeCppNative.StreamUpdate();
            TranscribeCppNative.transcribe_stream_update_init(ref upd);
            var st = TranscribeCppNative.transcribe_stream_feed(_session, chunk, count, ref upd);
            if (st != 0)
                throw new TranscribeCppException($"stream_feed failed: {TranscribeCppNative.Status(st)}");
            if (upd.result_changed == 0) return null;

            // Copy strings IMMEDIATELY — pointers die on the next feed/finalize.
            var txt = new TranscribeCppNative.StreamText();
            TranscribeCppNative.transcribe_stream_text_init(ref txt);
            if (TranscribeCppNative.transcribe_stream_get_text(_session, ref txt) != 0) return null;
            var committed = TranscribeCppNative.Str(txt.committed_text);
            if (committed == _lastCommitted) return null;
            _lastCommitted = committed;
            return committed;
        }

        public (string Text, bool WasTruncated) Finalize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var upd = new TranscribeCppNative.StreamUpdate();
            TranscribeCppNative.transcribe_stream_update_init(ref upd);
            var st = TranscribeCppNative.transcribe_stream_finalize(_session, ref upd);
            if (st != 0)
                throw new TranscribeCppException($"stream_finalize failed: {TranscribeCppNative.Status(st)}");
            var txt = new TranscribeCppNative.StreamText();
            TranscribeCppNative.transcribe_stream_text_init(ref txt);
            var stTxt = TranscribeCppNative.transcribe_stream_get_text(_session, ref txt);
            if (stTxt != 0)
                throw new TranscribeCppException($"stream_get_text failed: {TranscribeCppNative.Status(stTxt)}");
            var full = TranscribeCppNative.Str(txt.full_text);
            return (full, TranscribeCppNative.transcribe_was_truncated(_session));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                TranscribeCppNative.transcribe_session_free(_session);
            }
            finally
            {
                _releaseComputeGate();   // exactly once — the compute gate frees here
            }
        }
    }
}
