using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using static Winpepper.Platform.Hotkeys.KeyboardHookNative;

namespace Winpepper.Platform.Hotkeys;

/// <summary>
/// Installs WH_KEYBOARD_LL on a dedicated STA thread, watches for the configured
/// chords, and emits <see cref="HotkeyEvent"/> instances on an unbounded channel.
/// </summary>
public sealed class HotkeyHook : IDisposable
{
    private readonly HotkeyChord _hold;
    private readonly HotkeyChord _toggle;
    private readonly HotkeyChord _cancel;
    private readonly ILogger<HotkeyHook> _log;

    private readonly Channel<HotkeyEvent> _events =
        Channel.CreateUnbounded<HotkeyEvent>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookHandle;
    private LowLevelKeyboardProc? _callback;
    private Modifier _modifiers;
    private bool _holding;
    private readonly ManualResetEventSlim _ready = new(initialState: false);

    public ChannelReader<HotkeyEvent> Events => _events.Reader;

    /// <summary>
    /// Public for tests: synchronously evaluate a key event against the registered
    /// chords. Used by integration tests that don't want to install a real hook.
    /// </summary>
    public bool TryProcessKey(int vk, bool down, out HotkeyEvent? evt)
    {
        evt = null;
        UpdateModifierState(vk, down);

        if (down)
        {
            if (_cancel.Matches(vk, _modifiers))
            {
                evt = new HotkeyEvent(HotkeyEventKind.Cancel, DateTimeOffset.UtcNow);
                return true;
            }
            if (_toggle.Matches(vk, _modifiers))
            {
                evt = new HotkeyEvent(HotkeyEventKind.Toggle, DateTimeOffset.UtcNow);
                return true;
            }
            if (_hold.Matches(vk, _modifiers) && !_holding)
            {
                _holding = true;
                evt = new HotkeyEvent(HotkeyEventKind.HoldDown, DateTimeOffset.UtcNow);
                return true;
            }
        }
        else if (_holding && !_hold.Matches(vk, _modifiers))
        {
            _holding = false;
            evt = new HotkeyEvent(HotkeyEventKind.HoldUp, DateTimeOffset.UtcNow);
            return true;
        }

        return false;
    }

    public HotkeyHook(HotkeyChord hold, HotkeyChord toggle, HotkeyChord cancel, ILogger<HotkeyHook> log)
    {
        _hold = hold; _toggle = toggle; _cancel = cancel; _log = log;
    }

    public void Start()
    {
        if (_hookThread != null) throw new InvalidOperationException("HotkeyHook already started.");
        _hookThread = new Thread(HookThread) { IsBackground = true, Name = "WinpepperHotkeyHook" };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("Hotkey hook failed to install within 5s.");
    }

    private void HookThread()
    {
        _hookThreadId = GetCurrentThreadId();
        _callback = HookCallback; // pin
        _hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, _callback, GetModuleHandleW(null), 0);
        if (_hookHandle == IntPtr.Zero)
        {
            _log.LogError("SetWindowsHookEx failed: 0x{Err:X}", Marshal.GetLastWin32Error());
            _ready.Set();
            return;
        }
        _ready.Set();
        _log.LogInformation("Hotkey hook installed on thread {Tid}", _hookThreadId);

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(msg);
            DispatchMessageW(msg);
        }

        UnhookWindowsHookEx(_hookHandle);
        _events.Writer.TryComplete();
        _log.LogInformation("Hotkey hook thread exiting");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != 0) return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var msg = (int)wParam;
        var down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        var up   = msg == WM_KEYUP   || msg == WM_SYSKEYUP;

        if (down || up)
        {
            if (TryProcessKey((int)data.VkCode, down, out var evt) && evt is not null)
            {
                _events.Writer.TryWrite(evt);
                // Swallow the chord so the foreground app doesn't see it.
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void UpdateModifierState(int vk, bool down)
    {
        var mod = vk switch
        {
            VK_LCONTROL => Modifier.LeftCtrl,
            VK_RCONTROL => Modifier.RightCtrl,
            VK_LSHIFT   => Modifier.LeftShift,
            VK_RSHIFT   => Modifier.RightShift,
            VK_LMENU    => Modifier.LeftAlt,
            VK_RMENU    => Modifier.RightAlt,
            VK_LWIN     => Modifier.LeftWin,
            VK_RWIN     => Modifier.RightWin,
            _           => Modifier.None,
        };
        if (mod == Modifier.None) return;
        if (down) _modifiers |= mod; else _modifiers &= ~mod;
    }

    public void Dispose()
    {
        if (_hookThread is null) return;
        PostThreadMessageW(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _hookThread.Join(TimeSpan.FromSeconds(2));
        _hookThread = null;
    }
}
