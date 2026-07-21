namespace Winpepper.Core.Pending;

/// <summary>
/// Pure, platform-agnostic identity of the field we intend to inject dictated
/// text into. Captured when dictation STARTS and re-captured at injection time
/// so the pipeline can tell whether focus is still on the same target. The
/// Windows layer builds this from a UIA focused-element snapshot (foreground
/// window handle + UIA RuntimeId joined with '.'); unit tests build it directly.
/// </summary>
public sealed record InjectionTarget
{
    /// <summary>Foreground window handle as a 64-bit value (IntPtr.ToInt64()). 0 when unknown.</summary>
    public required long WindowHandle { get; init; }

    /// <summary>Opaque focused-element identity (UIA RuntimeId joined with '.'). Empty when unknown.</summary>
    public required string ElementId { get; init; }

    /// <summary>True when we captured a usable element identity.</summary>
    public bool IsValid => !string.IsNullOrEmpty(ElementId);

    /// <summary>True when both targets refer to the same window AND the same focused element.</summary>
    public bool Matches(InjectionTarget other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return WindowHandle == other.WindowHandle
            && string.Equals(ElementId, other.ElementId, StringComparison.Ordinal);
    }

    public static InjectionTarget Empty { get; } = new() { WindowHandle = 0, ElementId = string.Empty };
}
