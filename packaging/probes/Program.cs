using System.Runtime.InteropServices;

namespace Winpepper.D3D12Probe;

internal static class Program
{
    private static int Main(string[] args)
    {
        var dx12 = HasDirectX12() ? "1" : "0";
        var sdk = HasWinAppSdk() ? "1" : "0";
        var build = ReadWindowsBuildNumber() ?? "0";

        var temp = Environment.GetEnvironmentVariable("TEMP")
                   ?? Path.GetTempPath();
        var path = Path.Combine(temp, "winpepper-probe.txt");
        File.WriteAllText(
            path,
            $"WINPEPPER_DX12_PRESENT={dx12}\r\n" +
            $"WINPEPPER_WINAPPSDK_PRESENT={sdk}\r\n" +
            $"MSI_WIN_BUILD={build}\r\n");
        return 0;
    }

    [DllImport("d3d12.dll", ExactSpelling = true)]
    private static extern int D3D12CreateDevice(
        IntPtr pAdapter,
        int MinimumFeatureLevel,
        ref Guid riid,
        IntPtr ppDevice);

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
}
