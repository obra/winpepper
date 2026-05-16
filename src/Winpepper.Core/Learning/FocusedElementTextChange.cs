namespace Winpepper.Core.Learning;

/// <summary>One text-change notification from the focused UIA element.</summary>
public sealed record FocusedElementTextChange(string ElementId, string NewText, DateTime TimestampUtc);
