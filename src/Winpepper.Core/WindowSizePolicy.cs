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
}
