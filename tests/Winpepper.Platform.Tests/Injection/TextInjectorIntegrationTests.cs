using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

[Trait("Platform", "Windows")]
public class TextInjectorIntegrationTests
{
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [Fact(Skip = "Requires interactive console window; run manually on VM with focus.")]
    public void Inject_Writes_To_Focused_Window()
    {
        if (!OperatingSystem.IsWindows()) return;
        var injector = new TextInjector(new NullLogger<TextInjector>());
        injector.TryInject("hello").ShouldBeTrue();
    }
}
