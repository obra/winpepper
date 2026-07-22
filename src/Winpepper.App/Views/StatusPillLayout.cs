namespace Winpepper.App.Views;

internal readonly record struct StatusPillPixelLayout(
    int ClientWidth,
    int ClientHeight,
    int BottomGap);

internal static class StatusPillLayout
{
    private const int DefaultDpi = 96;
    private const int ClientWidthDip = 300;   // widened from 260 for the voice meter
    private const int ClientHeightDip = 48;
    private const int BottomGapDip = 48;

    public static StatusPillPixelLayout ForDpi(uint dpi)
    {
        if (dpi == 0)
            throw new ArgumentOutOfRangeException(nameof(dpi));

        return new StatusPillPixelLayout(
            ScaleToPixels(ClientWidthDip, dpi),
            ScaleToPixels(ClientHeightDip, dpi),
            ScaleToPixels(BottomGapDip, dpi));
    }

    private static int ScaleToPixels(int dips, uint dpi) =>
        checked((int)(((long)dips * dpi + DefaultDpi / 2) / DefaultDpi));
}
