namespace Winpepper.Core.ViewModels;

public sealed record OnboardingDownloadState(
    double ProgressPercent,        // 0..100 aggregate across the batch, byte-weighted
    string StatusText,             // e.g. "Downloading English speech model…", "All models verified — ready to dictate."
    string? Error,                 // sticky until the next StartDownloads
    bool SpeechModelReady);        // true only after the speech model's FILES verified AND a one-shot
                                   // ENGINE LOAD PROBE succeeded (spawn worker -> Load -> dispose);
                                   // only then may the pipeline start

/// <summary>Background, multi-model onboarding downloads. StartDownloads never
/// throws and never blocks the caller; it downloads the SPEECH model first
/// (it gates Test dictation), then the optional models, publishing progress
/// via StateChanged. SpeechModelReady requires BOTH file verification
/// (size + SHA-256 + extraction) AND a successful one-shot ENGINE LOAD PROBE
/// (spawn a worker for the selected layout, issue Load, dispose) — file checks
/// alone cannot see a missing VC++ redistributable, a model/runtime ABI
/// mismatch, or a worker spawn failure, so this closes the "onboarding says
/// ready but the first dictation fails" hole (V6/A16). On probe failure the
/// provisioner publishes a sticky Error with actionable text. Calling
/// StartDownloads again while a run is active is a no-op join; calling it
/// after a failure retries. The underlying downloads survive the caller
/// navigating away (coordinator/downloader semantics).</summary>
public interface IOnboardingModelProvisioner
{
    OnboardingDownloadState State { get; }
    event EventHandler<OnboardingDownloadState>? StateChanged;
    void StartDownloads(IReadOnlyList<string> modelNames, string speechModelName);
}
