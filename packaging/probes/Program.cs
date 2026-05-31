using System.Runtime.InteropServices;

namespace Winpepper.D3D12Probe;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--warn-no-dx12", StringComparison.OrdinalIgnoreCase))
        {
            WarnIfDirectX12Missing();
            return 0;
        }

        var dx12 = HasDirectX12() ? "1" : "0";
        var sdk = HasWinAppSdk() ? "1" : "0";
        var build = ReadWindowsBuildNumber() ?? "0";

        WriteProbeOutput(dx12, sdk, build);
        return 0;
    }

    [DllImport("d3d12.dll", ExactSpelling = true)]
    private static extern int D3D12CreateDevice(
        IntPtr pAdapter,
        int MinimumFeatureLevel,
        ref Guid riid,
        IntPtr ppDevice);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(
        IntPtr hWnd,
        string text,
        string caption,
        uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONWARNING = 0x00000030;

    private const int D3D_FEATURE_LEVEL_12_0 = 0xC000;

    private static bool HasDirectX12()
    {
        try
        {
            var iid = new Guid("189819F1-1DB6-4B57-BE54-1821339B85F7"); // ID3D12Device
            var hr = D3D12CreateDevice(IntPtr.Zero, D3D_FEATURE_LEVEL_12_0, ref iid, IntPtr.Zero);
            return hr == 0 || hr == 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasWinAppSdk()
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine,
                Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\WindowsAppRuntime\Installed\1.6");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadWindowsBuildNumber()
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine,
                Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion");
            var v = key?.GetValue("CurrentBuildNumber") as string;
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static void WriteProbeOutput(string dx12, string sdk, string build)
    {
        var temp = Environment.GetEnvironmentVariable("TEMP")
                   ?? Path.GetTempPath();
        var path = Path.Combine(temp, "winpepper-probe.txt");
        File.WriteAllText(
            path,
            $"WINPEPPER_DX12_PRESENT={dx12}\r\n" +
            $"WINPEPPER_WINAPPSDK_PRESENT={sdk}\r\n" +
            $"MSI_WIN_BUILD={build}\r\n");
    }

    private static void WarnIfDirectX12Missing()
    {
        if (HasDirectX12())
        {
            return;
        }

        MessageBoxW(
            IntPtr.Zero,
            "DirectX 12 is not available on this system. Winpepper will run on CPU; voice input will be slower. The app will still install.",
            "Winpepper",
            MB_OK | MB_ICONWARNING);
    }
}
