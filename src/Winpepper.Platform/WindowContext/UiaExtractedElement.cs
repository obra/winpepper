namespace Winpepper.Platform.WindowContext;

/// <summary>
/// One piece of text recovered from a UIA tree element, with the element's
/// top-left position in screen coordinates for reading-order sorting.
/// </summary>
public sealed record UiaExtractedElement(
    string Text,
    int BoundingLeft,
    int BoundingTop);
