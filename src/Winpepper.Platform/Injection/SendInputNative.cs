using System.Runtime.InteropServices;

namespace Winpepper.Platform.Injection;

internal static partial class SendInputNative
{
    public const int INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP   = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int Dx, Dy; public uint MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT { public uint Msg; public ushort WParamL; public ushort WParamH; }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public KEYBDINPUT Keyboard;
        [FieldOffset(8)] public MOUSEINPUT Mouse;
        [FieldOffset(8)] public HARDWAREINPUT Hardware;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr GetForegroundWindow();
}
