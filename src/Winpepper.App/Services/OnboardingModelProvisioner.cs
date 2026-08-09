#if WINDOWS
using Microsoft.Extensions.Logging;
using Winpepper.Core.ViewModels;
using Winpepper.Models;
using Winpepper.Models.ViewModels;

namespace Winpepper.App.Services;

/// <summary>
/// Background multi-model downloads for onboarding. Speech model first (it
/// gates Test dictation), optional models after. Serializes with the Models
/// page and StreamingAutoInstaller via the shared per-downloader operation
/// gate, so nothing double-downloads. Never throws; errors surface in State.
/// "Verified" for the speech model = per-file size + SHA-256 + extraction
/// (the bar the old blocking Step 3 enforced) PLUS a one-shot ENGINE LOAD
/// PROBE (spawn worker -> Load -> dispose, injected as a delegate so this
/// class stays testable) — file checks cannot see a missing VC++
/// redistributable, an ABI mismatch, or a spawn failure (V6/A16).
/// StateChanged is raised on the UI thread via the DispatcherQueue captured
/// at construction: WinUI bindings are thread-affine, and subscribers (the
/// onboarding VM) mutate bound properties — raising from the download thread
/// would risk RPC_E_WRONG_THREAD (V2/A12).
/// </summary>
public sealed class OnboardingModelProvisioner : IOnboardingModelProvisioner
{
    private readonly ModelsServices _models;
    private readonly ILogger _log;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private readonly Func<string, CancellationToken, Task<bool>> _engineLoadProbe;
    private readonly object _gate = new();
    private Task? _run;
    private OnboardingDownloadState _state = new(0, "Waiting to download", null, false);

    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.Ordinal)
    {
        [ModelRegistry.StreamingAsrName] = "English speech model",
        [ModelRegistry.MultilingualStreamingAsrName] = "Multilingual speech model",
        [ModelRegistry.DefaultAsrName] = "backup speech model",
        [ModelRegistry.DefaultCleanupName] = "text cleanup model",
    };

    public OnboardingModelProvisioner(ModelsServices models, ILogger log,
        Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue,
        Func<string, CancellationToken, Task<bool>> engineLoadProbe)
    {
        _models = models;
        _log = log;
        _dispatcherQueue = dispatcherQueue;   // captured at construction (AppShell.Create runs on the UI thread)
        _engineLoadProbe = engineLoadProbe;   // AppShell injects the worker-engine-based probe
    }

    public OnboardingDownloadState State { get { lock (_gate) return _state; } }
    public event EventHandler<OnboardingDownloadState>? StateChanged;

    public void StartDownloads(IReadOnlyList<string> modelNames, string speechModelName)
    {
        lock (_gate)
        {
            if (_run is { IsCompleted: false }) return; // join the active run
            _run = Task.Run(() => RunAsync(modelNames.ToArray(), speechModelName));
        }
    }

    private async Task RunAsync(IReadOnlyList<string> names, string speechModelName)
    {
        // A NEW run (retry included) must RE-VERIFY: reset to a fresh
        // non-ready state so this run cannot inherit a stale
        // SpeechModelReady/Error from a previous run (Publish's monotonic
        // OR is per-run, not per-instance).
        lock (_gate) _state = new OnboardingDownloadState(0, "Waiting to download", null, false);
        try
        {
            var registry = _models.Registry;
            var root = _models.ModelsRoot;
            var plan = DownloadBatchPlanner.Plan(registry, root, names, speechModelName);
            // Track the WHOLE selection (installed members count as done bytes).
            var selection = names.Select(registry.Find).Where(d => d is not null).Select(d => d!).ToList();
            var done = selection.ToDictionary(d => d.Name,
                d => plan.Any(p => p.Name == d.Name) ? 0L : d.TotalSizeBytes);

            // The file's own invariant (see the progress callback below):
            // EVERY read of `done` for a percent snapshot must happen under
            // _gate — straggler Progress<> callbacks keep writing `done`
            // after DownloadAsync returns.
            double SnapshotPercent() { lock (_gate) return Percent(selection, done); }

            var opGate = ModelsTabViewModel.SharedOperationGateFor(_models);
            foreach (var descriptor in plan)
            {
                Publish(SnapshotPercent(), $"Downloading {Friendly(descriptor)}…", null, false);
                await opGate.WaitAsync();
                try
                {
                    var progress = new Progress<DownloadProgress>(p =>
                    {
                        // per-file bytes -> per-descriptor tally (sum file dones);
                        // the percent snapshot must be computed under the same
                        // lock — concurrent callbacks racing an unlocked read
                        // of `done` would be a data race.
                        double percent;
                        lock (_gate)
                        {
                            done[descriptor.Name] = TallyFor(descriptor, p, done[descriptor.Name]);
                            percent = Percent(selection, done);
                        }
                        Publish(percent, $"Downloading {Friendly(descriptor)}…", null,
                            SpeechReadyNow(speechModelName));
                    });
                    await _models.DownloadAsync(descriptor, root, progress, CancellationToken.None);
                    // Straggler progress callbacks may still be in flight: write
                    // the final tally under the same lock they use.
                    lock (_gate) done[descriptor.Name] = descriptor.TotalSizeBytes;
                }
                finally { opGate.Release(); }

                if (descriptor.Name == speechModelName)
                {
                    Publish(SnapshotPercent(), "Verifying speech model…", null, false);
                    var error = await VerifySpeechDeepAsync(speechModelName);
                    if (error is not null)
                    {
                        Publish(SnapshotPercent(), "Speech model failed verification.", error, false);
                        return;
                    }
                    Publish(SnapshotPercent(), "Speech model ready — keep going while the rest downloads.", null, true);
                }
            }

            // Plan may be empty (everything installed) — still verify + probe the speech model.
            if (!State.SpeechModelReady)
            {
                var error = await VerifySpeechDeepAsync(speechModelName);
                if (error is not null)
                {
                    Publish(100, "Speech model failed verification.", error, false);
                    return;
                }
            }
            Publish(100, "All models verified — ready to dictate.", null, true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "onboarding model download failed");
            Publish(State.ProgressPercent, "Download failed.", ex.Message, State.SpeechModelReady);
        }
    }

    /// <summary>Speech readiness = files verified AND a one-shot ENGINE LOAD
    /// PROBE (spawn a worker for the selected layout, issue Load, dispose).
    /// File checks alone cannot see a missing VC++ redistributable, a
    /// model/runtime ABI mismatch, or a worker spawn failure (V6/A16) — the
    /// probe closes the "onboarding says ready but the first dictation
    /// fails" hole. Returns null when ready, else sticky actionable error text.</summary>
    private async Task<string?> VerifySpeechDeepAsync(string speechModelName)
    {
        var d = _models.Registry.Find(speechModelName);
        if (d is null) return "The speech model could not be verified. Retry the download.";
        var filesOk = await ModelFilesVerifier.VerifyAsync(d, _models.ModelsRoot, CancellationToken.None)
                      && (d.Kind != ModelKind.StreamingAsr || d.IsFullyInstalledAndExtracted(_models.ModelsRoot));
        if (!filesOk) return "The speech model could not be verified. Retry the download.";
        Publish(State.ProgressPercent, "Checking the speech engine…", null, false);
        var probeOk = await _engineLoadProbe(speechModelName, CancellationToken.None);
        if (!probeOk)
            return $"The {Friendly(d)} downloaded and verified, but its speech engine failed to load. " +
                   "Open Settings > Models to repair it. A missing Microsoft Visual C++ x64 Redistributable " +
                   "is the most common cause.";
        return null;
    }

    private bool SpeechReadyNow(string speechModelName) => State.SpeechModelReady; // monotonic within a run

    private static string Friendly(ModelDescriptor d)
        => FriendlyNames.TryGetValue(d.Name, out var n) ? n : d.DisplayName;

    private static double Percent(IReadOnlyList<ModelDescriptor> selection, IReadOnlyDictionary<string, long> done)
        => DownloadBatchPlanner.AggregatePercent(
            selection.Select(d => (d.TotalSizeBytes, done.GetValueOrDefault(d.Name))).ToList());

    private static long TallyFor(ModelDescriptor d, DownloadProgress p, long previous)
    {
        // DownloadProgress is per-file; approximate the descriptor tally as
        // completed-files bytes + current file's BytesDownloaded. Files
        // download sequentially, so summing monotonically is safe:
        var precedingBytes = 0L;
        foreach (var f in d.Files)
        {
            if (f.RelativePath == p.FileRelativePath)
                return Math.Max(previous, precedingBytes + Math.Clamp(p.BytesDownloaded, 0, f.SizeBytes));
            precedingBytes += f.SizeBytes;
        }
        return previous;
    }

    private void Publish(double percent, string status, string? error, bool speechReady)
    {
        OnboardingDownloadState s;
        lock (_gate)
        {
            // SpeechModelReady is MONOTONIC within a run: once true it stays
            // true (later optional-model errors must not re-lock the gate).
            var ready = speechReady || _state.SpeechModelReady;
            s = _state = new OnboardingDownloadState(percent, status, error, ready);
        }
        // WinUI bindings are thread-affine: subscribers (the onboarding VM)
        // mutate bound properties, so StateChanged must be raised on the UI
        // thread — anything else risks RPC_E_WRONG_THREAD (V2/A12).
        _dispatcherQueue.TryEnqueue(() => StateChanged?.Invoke(this, s));
    }
}
#endif
