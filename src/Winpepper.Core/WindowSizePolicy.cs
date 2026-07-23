namespace Winpepper.Core;

/// <summary>
/// Pure sizing policy for the main window (spec Task 4): default to about a
/// third of the platform default width and half its height, clamped to a
/// usable minimum so the nav UI stays usable on small screens.
/// </summary>
public static class WindowSizePolicy
{
    public static (int Width, int Height) ComputeDefault(
        int platformWidth, int platformHeight, int minWidth = 480, int minHeight = 400)
    {
        var w = Math.Max(platformWidth / 3, minWidth);
        var h = Math.Max(platformHeight / 2, minHeight);
        return (w, h);
    }

    /// <summary>
    /// Whether a window size is worth persisting/restoring. Minimized windows
    /// report a caption-strip rect (~160x28 at 96 DPI); persisting that and
    /// restoring it later yields a window that barely fits the caption buttons.
    /// Anything below the usable minimum is rejected both when saving and when
    /// restoring.
    /// </summary>
    public static bool IsSaneSize(int width, int height, int minWidth = 480, int minHeight = 400)
        => width >= minWidth && height >= minHeight;
}
