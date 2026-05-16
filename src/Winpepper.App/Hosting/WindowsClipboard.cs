#if WINDOWS
using Windows.ApplicationModel.DataTransfer;
using Winpepper.Platform.Injection;

namespace Winpepper.App.Hosting;

public sealed class WindowsClipboard : IClipboard
{
    public bool SetText(string text)
    {
        var pkg = new DataPackage();
        pkg.SetText(text);
        Clipboard.SetContent(pkg);
        return true;
    }
}
#endif
