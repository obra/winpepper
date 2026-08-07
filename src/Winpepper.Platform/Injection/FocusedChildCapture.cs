namespace Winpepper.Platform.Injection;

/// <summary>
/// Result of the send-start focused-child capture (design doc §2.2
/// hardening, motivated by the E7c wrong-target anomalies).
/// FocusedChildHwnd is the EFFECTIVE handle: 0 whenever the double-sample
/// was unstable or either sample was zero — that convention is how the
/// two-long IDeliveryStrategy.CanDeliver contract "receives the stability
/// fact". Invariant: !Stable implies FocusedChildHwnd == 0.
/// </summary>
public readonly record struct FocusedChildCapture(long FocusedChildHwnd, bool Stable);
