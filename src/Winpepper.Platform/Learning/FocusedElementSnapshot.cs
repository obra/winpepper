namespace Winpepper.Platform.Learning;

/// <summary>What we know about the focused element at injection time. Spec §8.2 (1).</summary>
public sealed record FocusedElementSnapshot
{
    public required IntPtr ForegroundHwnd { get; init; }
    public required string ElementId { get; init; }
    public required string WindowTitle { get; init; }

    public bool IsValid => !string.IsNullOrEmpty(ElementId);

    public static FocusedElementSnapshot Empty { get; } = new()
    {
        ForegroundHwnd = IntPtr.Zero,
        ElementId = string.Empty,
        WindowTitle = string.Empty,
    };
}
