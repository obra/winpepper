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
    private sealed record HotkeyBindings(HotkeyChord Hold, HotkeyChord Toggle, HotkeyChord Cancel);
    private sealed record RawCaptureRegistration(Action<RawKeyTransition> Sink);

    private sealed class RawCaptureLease : IDisposable
    {
        private HotkeyHook? _owner;
        private readonly RawCaptureRegistration _registration;

        public RawCaptureLease(HotkeyHook owner, RawCaptureRegistration registration)
        {
            _owner = owner;
            _registration = registration;
        }

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.EndRawCapture(_registration);
    }

    private HotkeyBindings _bindings;
    private readonly Func<bool> _cancelEnabled;
    private readonly ILogger<HotkeyHook> _log;

    private readonly Channel<HotkeyEvent> _events =
        Channel.CreateUnbounded<HotkeyEvent>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookHandle;
    private LowLevelKeyboardProc? _callback;
    private Modifier _modifiers;
    private bool _holding;
    // vk -> timestamp the swallow was last observed. Entries self-heal (drop)
    // when the physical key is no longer held, so a lost key-up can never
    // swallow a key forever.
    private readonly Dictionary<int, DateTimeOffset> _swallowedKeys = new();
    // vk -> timestamp a physical down was deliberately passed through because
    // normal processing was unavailable. Once Windows sees the down, repeats
    // and the matching up must also pass even if the gate changes.
    // Physical-state pruning heals a lost key-up before a later fresh press.
    private readonly Dictionary<int, DateTimeOffset> _passedThroughKeys = new();
    private readonly HashSet<int> _observedCancelKeys = new();
    // vk -> timestamp last observed during capture. Same physical-state
    // self-heal as _swallowedKeys: a lost key-up must not wedge drain mode.
    private readonly Dictionary<int, DateTimeOffset> _captureKeysDown = new();
    private readonly object _captureGate = new();
    private RawCaptureRegistration? _rawCapture;
    private int _suspendRequested;
    private readonly ManualResetEventSlim _ready = new(initialState: false);

    private readonly Func<DateTimeOffset> _now;
    private readonly Func<int, bool> _keyPhysicallyDown;
    private readonly Func<bool> _normalTriggersEnabled;
    private readonly LongPressSpaceStateMachine _spaceHold;

    public ChannelReader<HotkeyEvent> Events => _events.Reader;

    /// <summary>
    /// Public for tests: synchronously evaluate a key event against the registered
    /// chords. The return value means "swallow this event" (hide it from the
    /// foreground app); <paramref name="evt"/> is set when a hotkey event fired.
    /// The two are independent: a key-up can emit HoldUp yet still pass through
    /// when its key-down was visible to the system.
    ///
    /// Invariant: modifier keys (Ctrl/Shift/Alt/Win, left or right) are NEVER
    /// swallowed. A modifier used in a hotkey still fires the event but always
    /// passes through to Windows so it keeps working system-wide. Only a
    /// non-modifier trigger key (e.g. the Space in Ctrl+Shift+Space) is
    /// swallowed.
    /// </summary>
    public bool TryProcessKey(int vk, bool down, out HotkeyEvent? evt,
        int scanCode = 0, bool isInjected = false)
    {
        evt = null;
        var now = _now();
        var modifiersBeforeEvent = _modifiers;
        var bindings = Volatile.Read(ref _bindings);

        // Low-level hook callbacks observe async key state before the current
        // transition is applied. A false Space state therefore proves any
        // previously owned press ended, while a repeat/up still reads down.
        _spaceHold.RecoverIfReleased();

        // A modifier pressed after a bare Space-down changes the user's intent.
        // Stop observing it as a hold while the already-visible physical press
        // continues to pass through to Windows.
        if (down && IsLongPressSpaceBinding(bindings.Hold) && IsModifierKey(vk))
            _spaceHold.CancelPendingForModifier();

        UpdateModifierState(vk, down);

        // Self-heal: drop tracked entries whose physical key is no longer held,
        // so a lost key-up can never leave a key swallowed or the hook wedged.
        // The current key is handled below.
        PruneStaleKeys(now, exceptVk: vk);

        var rawCapture = Volatile.Read(ref _rawCapture);
        if (rawCapture is not null)
        {
            var swallowActiveSpace = vk == VirtualKeyCatalog.Space
                && _spaceHold.IsActive
                && _spaceHold.Process(down);
            var isRepeat = down && _captureKeysDown.ContainsKey(vk);
            if (down) _captureKeysDown[vk] = now;
            else _captureKeysDown.Remove(vk);

            try
            {
                rawCapture.Sink(new RawKeyTransition(vk, scanCode, down, isInjected, isRepeat));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Raw hotkey capture callback failed");
            }

            _observedCancelKeys.Remove(vk);
            if (!isInjected)
            {
                if (down && _passedThroughKeys.ContainsKey(vk))
                    _passedThroughKeys[vk] = now;
                else if (!down)
                    _passedThroughKeys.Remove(vk);
            }
            var swallowWhileCaptured = !down && _swallowedKeys.Remove(vk);
            if (!down && _holding && HoldEndedOnKeyUp(bindings.Hold, vk))
            {
                _holding = false;
                evt = new HotkeyEvent(HotkeyEventKind.HoldUp, DateTimeOffset.UtcNow);
            }
            return swallowActiveSpace || swallowWhileCaptured;
        }

        // Once a bare Space-down is being observed, retain its state across
        // readiness/binding changes. Only typematic downs during an active hold
        // are suppressed; the original down and physical up always pass.
        if (vk == VirtualKeyCatalog.Space && _spaceHold.IsActive)
            return _spaceHold.Process(down);

        var suspendRequested = Volatile.Read(ref _suspendRequested) != 0;
        if (suspendRequested || _captureKeysDown.Count != 0)
        {
            // Keep passing through every key involved in capture until all of
            // them are released. This drain phase prevents typematic repeats
            // from firing a just-recorded chord after the UI requests resume.
            if (down)
            {
                _captureKeysDown[vk] = now;
                if (!isInjected && _passedThroughKeys.ContainsKey(vk))
                    _passedThroughKeys[vk] = now;
                return false;
            }
            _captureKeysDown.Remove(vk);
            if (!isInjected) _passedThroughKeys.Remove(vk);
            _observedCancelKeys.Remove(vk);

            // Finish any chord that was already active when capture began, but
            // otherwise pass keys through so the settings control can see them.
            var swallowWhileSuspended = _swallowedKeys.Remove(vk);
            if (_holding && HoldEndedOnKeyUp(bindings.Hold, vk))
            {
                _holding = false;
                evt = new HotkeyEvent(HotkeyEventKind.HoldUp, DateTimeOffset.UtcNow);
            }
            return swallowWhileSuspended;
        }

        // A physical down that reached Windows while a gate was closed owns a
        // pass-through sequence until its physical up. A typematic repeat must
        // not become a new trigger merely because readiness changed mid-press.
        // For a current down, a false physical probe means the prior up was lost
        // and this is a fresh press.
        if (_passedThroughKeys.TryGetValue(vk, out var passedSince))
        {
            if (isInjected) return false;
            if (down && IsKeyEntryLive(vk, passedSince, now))
            {
                _passedThroughKeys[vk] = now;
                return false;
            }

            _passedThroughKeys.Remove(vk);
            if (!down)
            {
                _observedCancelKeys.Remove(vk);
                return false;
            }
        }

        // Model-less onboarding still needs the hook for focus-independent raw
        // capture. Gate normal matching here, before any new trigger can be
        // emitted or swallowed, while finishing ownership from an earlier
        // enabled press symmetrically.
        if (!NormalTriggersEnabled())
        {
            _observedCancelKeys.Remove(vk);
            if (down && _swallowedKeys.TryGetValue(vk, out var swallowedSince))
            {
                if (IsKeyEntryLive(vk, swallowedSince, now))
                {
                    _swallowedKeys[vk] = now;
                    return true;
                }
                _swallowedKeys.Remove(vk);
            }

            var swallowOwnedUp = !down && _swallowedKeys.Remove(vk);
            if (!down && _holding && HoldEndedOnKeyUp(bindings.Hold, vk))
            {
                _holding = false;
                evt = new HotkeyEvent(HotkeyEventKind.HoldUp, DateTimeOffset.UtcNow);
            }
            if (down && !isInjected) _passedThroughKeys[vk] = now;
            return swallowOwnedUp;
        }

        if (IsLongPressSpaceBinding(bindings.Hold)
            && vk == VirtualKeyCatalog.Space
            && down
            && _modifiers == Modifier.None)
            return _spaceHold.Process(down);

        if (down)
        {
            // Autorepeat of a key whose down we swallowed: keep swallowing it,
            // but do not emit a duplicate event. Without this, repeats of the
            // chord-completing modifier leak to the foreground app, and a held
            // toggle chord fires Toggle once per repeat.
            // Autorepeat of a key we own: keep swallowing while it is live, and
            // refresh its liveness timestamp. If the entry is stale (lost
            // key-up), drop it and treat this as a fresh press below.
            if (_swallowedKeys.TryGetValue(vk, out var swallowedSince))
            {
                if (IsKeyEntryLive(vk, swallowedSince, now))
                {
                    _swallowedKeys[vk] = now;
                    return true;
                }
                _swallowedKeys.Remove(vk);
            }

            if (CancelEnabled()
                && ActivatesOnKeyDown(bindings.Cancel, vk, modifiersBeforeEvent, _modifiers))
            {
                if (_observedCancelKeys.Add(vk))
                    evt = new HotkeyEvent(HotkeyEventKind.Cancel, DateTimeOffset.UtcNow);
                return false;
            }
            if (ActivatesOnKeyDown(bindings.Toggle, vk, modifiersBeforeEvent, _modifiers))
            {
                evt = new HotkeyEvent(HotkeyEventKind.Toggle, DateTimeOffset.UtcNow);
                // Modifier keys always pass through to Windows so they keep
                // working system-wide (e.g. Shift still shifts). Only a
                // non-modifier trigger key is hidden from the foreground app.
                if (IsModifierKey(vk)) return false;
                _swallowedKeys[vk] = now;
                return true;
            }
            if (!IsLongPressSpaceBinding(bindings.Hold)
                && ActivatesOnKeyDown(bindings.Hold, vk, modifiersBeforeEvent, _modifiers)
                && !_holding)
            {
                _holding = true;
                evt = new HotkeyEvent(HotkeyEventKind.HoldDown, DateTimeOffset.UtcNow);
                // Modifier keys always pass through (see the Toggle branch
                // above); only a non-modifier trigger key is swallowed.
                if (IsModifierKey(vk)) return false;
                _swallowedKeys[vk] = now;
                return true;
            }
            return false;
        }

        // Key up: swallow only when we swallowed the matching key-down, so the
        // system's view of every physical key stays down/up symmetric. Swallowing
        // an up whose down passed through leaves that key logically stuck down
        // system-wide (e.g. Shift stuck after a RightCtrl+RightShift hold chord).
        _observedCancelKeys.Remove(vk);
        var swallow = _swallowedKeys.Remove(vk);
        if (_holding && HoldEndedOnKeyUp(bindings.Hold, vk))
        {
            _holding = false;
            evt = new HotkeyEvent(HotkeyEventKind.HoldUp, DateTimeOffset.UtcNow);
        }
        return swallow;
    }

    public HotkeyHook(
        HotkeyChord hold,
        HotkeyChord toggle,
        HotkeyChord cancel,
        ILogger<HotkeyHook> log,
        Func<bool>? cancelEnabled = null,
        Func<DateTimeOffset>? timeProvider = null,
        Func<int, bool>? keyPhysicallyDown = null,
        ILongPressTimerScheduler? spaceTimerScheduler = null,
        Func<bool>? normalTriggersEnabled = null)
    {
        _bindings = new HotkeyBindings(hold, toggle, cancel);
        _log = log;
        _cancelEnabled = cancelEnabled ?? (() => true);
        _now = timeProvider ?? (() => DateTimeOffset.UtcNow);
        _keyPhysicallyDown = keyPhysicallyDown ?? DefaultKeyPhysicallyDown;
        _normalTriggersEnabled = normalTriggersEnabled ?? (() => true);
        _spaceHold = new LongPressSpaceStateMachine(
            spaceTimerScheduler ?? new SystemLongPressTimerScheduler(),
            kind => _events.Writer.TryWrite(new HotkeyEvent(kind, _now())),
            isSpacePhysicallyDown: () => _keyPhysicallyDown(VirtualKeyCatalog.Space),
            canStartHold: NormalTriggersEnabled);
    }

    // Real physical key-state probe. Guarded so it is only P/Invoked on Windows;
    // on other platforms (Linux test host) production never installs the hook,
    // and unit tests inject their own probe, so returning true here is inert.
    private static bool DefaultKeyPhysicallyDown(int vk)
        => !OperatingSystem.IsWindows() || (GetAsyncKeyState(vk) & 0x8000) != 0;

    private bool CancelEnabled()
    {
        try
        {
            return _cancelEnabled();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cancel hotkey gate threw; ignoring cancel chord");
            return false;
        }
    }

    private bool NormalTriggersEnabled()
    {
        try
        {
            return _normalTriggersEnabled();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Normal hotkey readiness gate threw; passing keys through");
            return false;
        }
    }

    /// <summary>Atomically replaces the active hold and toggle chords.</summary>
    public void UpdateChords(HotkeyChord hold, HotkeyChord toggle)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ArgumentNullException.ThrowIfNull(toggle);

        var current = Volatile.Read(ref _bindings);
        if (IsLongPressSpaceBinding(current.Hold) && !IsLongPressSpaceBinding(hold))
            _spaceHold.Cancel();
        Volatile.Write(ref _bindings, new HotkeyBindings(hold, toggle, current.Cancel));
        _log.LogInformation("Hotkeys updated: hold={Hold}, toggle={Toggle}", hold, toggle);
    }

    /// <summary>
    /// Lets the settings UI receive key events without the global hook firing
    /// or swallowing the chord currently being captured.
    /// </summary>
    public void SetSuspended(bool suspended)
    {
        if (suspended) _spaceHold.Cancel();
        Volatile.Write(ref _suspendRequested, suspended ? 1 : 0);
        _log.LogDebug("Hotkey hook {State} for chord capture", suspended ? "suspended" : "resumed");
    }

    /// <summary>
    /// Acquires exclusive focus-independent raw keyboard capture. Captured keys
    /// pass through to Windows and bypass configured trigger processing.
    /// </summary>
    public IDisposable BeginRawCapture(Action<RawKeyTransition> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _spaceHold.Cancel();
        lock (_captureGate)
        {
            if (_rawCapture is not null)
                throw new InvalidOperationException("A raw hotkey capture lease is already active.");
            var registration = new RawCaptureRegistration(sink);
            Volatile.Write(ref _rawCapture, registration);
            _log.LogDebug("Raw hotkey capture acquired");
            return new RawCaptureLease(this, registration);
        }
    }

    private void EndRawCapture(RawCaptureRegistration registration)
    {
        lock (_captureGate)
        {
            if (!ReferenceEquals(_rawCapture, registration)) return;
            Volatile.Write(ref _rawCapture, null);
            _log.LogDebug("Raw hotkey capture released");
        }
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
            var injected = (data.Flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0;
            var swallow = TryProcessKey((int)data.VkCode, down, out var evt,
                (int)data.ScanCode, injected);
            if (evt is not null) _events.Writer.TryWrite(evt);
            // Swallow chord keys we own so the foreground app doesn't see them,
            // but never hide a key-up whose key-down already reached the system.
            if (swallow) return (IntPtr)1;
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void UpdateModifierState(int vk, bool down)
    {
        var mod = ModifierForVirtualKey(vk);
        if (mod == Modifier.None) return;
        if (down) _modifiers |= mod; else _modifiers &= ~mod;
    }

    private static bool ActivatesOnKeyDown(HotkeyChord chord, int vk,
                                           Modifier modifiersBeforeEvent,
                                           Modifier currentModifiers)
    {
        if (chord.VirtualKey != 0)
            return chord.Matches(vk, currentModifiers);

        // Modifier-only chords are state matches, so Matches alone would also
        // return true for an unrelated key pressed while the modifiers remain
        // held. Activate only when this modifier keydown changes the chord from
        // incomplete to complete.
        var pressedModifier = ModifierForVirtualKey(vk);
        return pressedModifier != Modifier.None
            && (chord.Modifiers & pressedModifier) != Modifier.None
            && !chord.Matches(0, modifiersBeforeEvent)
            && chord.Matches(0, currentModifiers);
    }

    private bool HoldEndedOnKeyUp(HotkeyChord hold, int releasedVirtualKey)
    {
        if (hold.VirtualKey != 0 && hold.VirtualKey == releasedVirtualKey) return true;
        var releasedModifier = ModifierForVirtualKey(releasedVirtualKey);
        return releasedModifier != Modifier.None
            && (hold.Modifiers & releasedModifier) != 0
            && !hold.Matches(hold.VirtualKey, _modifiers);
    }

    private static bool IsLongPressSpaceBinding(HotkeyChord chord)
        => chord.Modifiers == Modifier.None && chord.VirtualKey == VirtualKeyCatalog.Space;

    /// <summary>
    /// A tracked key entry is "live" while the physical key is still held.
    /// Age alone cannot make a held key stale: accessibility and typematic
    /// settings may delay autorepeat for an arbitrary amount of time.
    /// </summary>
    private bool IsKeyEntryLive(int vk, DateTimeOffset _, DateTimeOffset __)
        => _keyPhysicallyDown(vk);

    /// <summary>
    /// Drops stale entries from the tracked key dictionaries, healing keys whose
    /// key-up was lost. <paramref name="exceptVk"/> is the key of the current
    /// event, which the normal down/up logic handles explicitly (so happy-path
    /// swallow/up symmetry is preserved). Excluding the current key is also
    /// correctness-critical on Windows: per the LowLevelKeyboardProc contract the
    /// callback runs BEFORE the current key's async state is updated, so a
    /// GetAsyncKeyState probe of that key would read a stale value; every OTHER
    /// tracked key's async state is already settled and safe to probe.
    /// </summary>
    private void PruneStaleKeys(DateTimeOffset now, int exceptVk)
    {
        PruneStale(_swallowedKeys, now, exceptVk);
        PruneStale(_captureKeysDown, now, exceptVk);
        PruneStale(_passedThroughKeys, now, exceptVk);
    }

    private void PruneStale(Dictionary<int, DateTimeOffset> keys, DateTimeOffset now, int exceptVk)
    {
        if (keys.Count == 0) return;
        List<int>? stale = null;
        foreach (var (vk, since) in keys)
        {
            if (vk == exceptVk) continue;
            if (!IsKeyEntryLive(vk, since, now)) (stale ??= new()).Add(vk);
        }
        if (stale is null) return;
        foreach (var vk in stale) keys.Remove(vk);
    }

    /// <summary>
    /// True when <paramref name="vk"/> is one of the eight modifier keys
    /// (Ctrl/Shift/Alt/Win, left or right). Modifier keys are always passed
    /// through to Windows so they keep functioning system-wide, even while the
    /// app uses them in a hotkey.
    /// </summary>
    private static bool IsModifierKey(int vk) => ModifierForVirtualKey(vk) != Modifier.None;

    private static Modifier ModifierForVirtualKey(int vk)
        => vk switch
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

    public void Dispose()
    {
        _spaceHold.Dispose();
        lock (_captureGate) Volatile.Write(ref _rawCapture, null);
        if (_hookThread is null) return;
        PostThreadMessageW(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _hookThread.Join(TimeSpan.FromSeconds(2));
        _hookThread = null;
    }
}
