namespace Winpepper.Platform.Injection;

/// <summary>
/// One rung of the injection delivery ladder (design doc 2026-08-06 §2.1).
/// Strategies deliver chunks only — chunking, the guarded run loop, halts,
/// pacing, elevation handling, and the failure pill are shared and
/// unchanged, and are NOT part of this contract.
/// </summary>
internal interface IDeliveryStrategy
{
    DeliveryChannel Channel { get; }

    /// Capability gate: runs ONCE at send start, before any text is sent.
    /// Must be side-effect-free on the target document (probing messages only).
    /// focusedChildHwnd is the EFFECTIVE capture result: 0 when the
    /// double-sample was unstable or empty (FocusedChildCapture invariant).
    bool CanDeliver(long foregroundHwnd, long focusedChildHwnd);

    /// Deliver one chunk. False = refused/failed -> the run STOPS and maps to
    /// the existing SendFailed flow (pill shows transcript with click-to-copy).
    /// Never throws for target-side failure.
    bool TrySendChunk(long targetHwnd, string chunk);
}
