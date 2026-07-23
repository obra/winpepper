using System;

namespace Winpepper.App.Views;

/// <summary>
/// The exclusive-coordinate rectangle and corner diameter for the pill's
/// rounded window region, in physical pixels. Right/Bottom are the exclusive
/// bounds to hand to CreateRoundRectRgn (so they equal the client width/height,
/// not width/height + 1). CornerDiameter is the ellipse diameter for the
/// rounded corners.
/// </summary>
public readonly record struct StatusPillRegionRect(
    int Left,
    int Top,
    int Right,
    int Bottom,
    int CornerDiameter);

/// <summary>
/// Pure geometry for the status pill's rounded window region. Kept free of any
/// Win32/WinUI types so it unit-tests on Linux. The window region must exactly
/// match the client rect (no overshoot) with a corner diameter equal to the
/// shorter client side, producing a true capsule silhouette at any DPI.
/// </summary>
public static class StatusPillRegionGeometry
{
    /// <summary>
    /// Compute the rounded-region rectangle. All inputs are physical pixels.
    /// <paramref name="windowLeft"/>/<paramref name="windowTop"/> come from
    /// GetWindowRect; <paramref name="clientOriginX"/>/<paramref name="clientOriginY"/>
    /// come from ClientToScreen(0,0); width/height come from GetClientRect.
    /// </summary>
    public static StatusPillRegionRect Compute(
        int windowLeft,
        int windowTop,
        int clientOriginX,
        int clientOriginY,
        int clientWidth,
        int clientHeight)
    {
        var left = clientOriginX - windowLeft;
        var top = clientOriginY - windowTop;
        var cornerDiameter = Math.Min(clientWidth, clientHeight);

        return new StatusPillRegionRect(
            Left: left,
            Top: top,
            Right: left + clientWidth,   // exclusive: exactly the client width
            Bottom: top + clientHeight,  // exclusive: exactly the client height
            CornerDiameter: cornerDiameter);
    }
}
