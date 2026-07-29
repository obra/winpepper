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
    private IntPtr _powerRegistration;
    // Held for the lifetime of the registration: the OS keeps a raw function
    // pointer to it, so letting the delegate be collected would crash on resume.
    private PowerNotificationNative.DeviceNotifyCallbackRoutine? _powerCallback;
    private LowLevelKeyboardProc? _callback;
    // Tick when the resume callback posted the reinstall message: a post that
    // is never followed by execution is the wedged-hook-thread signature, so
    // the dequeue latency is logged (Step 4d).
    private long _reinstallRequestedTick;
    // Heartbeat TELEMETRY (Step 4f): tick of the last time the OS actually
    // called the hook. NOT a trigger - it turns uncovered hook deaths (any
    // >=1000 ms callback timeout, not only sleep/resume) into log evidence.
    private long _lastHookCallbackTick = Environment.TickCount64;
    private Timer? _heartbeatTimer;
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
    private readonly Action _beforeLongPressSpaceAdmission;
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

        // Injected fast-path (standard WH_KEYBOARD_LL practice, 2026-07-28):
        // synthetic events (LLKHF_INJECTED / LLKHF_LOWER_IL_INJECTED --
        // SendInput from ANY process, including our own TextInjector and
        // NeutralizeHeldModifiers) never participate in chord matching or
        // key-state tracking; they pass straight through. This (a) removes
        // the hook's per-event tax (~0.2 ms/event measured on the production
        // host) from every injected keystroke system-wide, and (b) fixes a
        // latent wedge: our own neutralization KEYUP for a physically-held
        // Win key used to clear _modifiers and end a Win-containing hold
        // chord spuriously. The chord recorder still receives injected
        // transitions (it filters them itself via RawKeyTransition.IsInjected),
        // so recording-mode behavior is contract-identical. _captureKeysDown
        // intentionally no longer tracks injected downs -- such entries were
        // self-healed anyway (GetAsyncKeyState reads them as up).
        if (isInjected)
        {
            var rawCaptureForInjected = Volatile.Read(ref _rawCapture);
            if (rawCaptureForInjected is not null)
            {
                try
                {
                    rawCaptureForInjected.Sink(
                        new RawKeyTransition(vk, scanCode, down, IsInjected: true, IsRepeat: false));
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Raw hotkey capture callback failed");
                }
            }
            return false; // never swallow synthetic input
        }

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
            if (down && _passedThroughKeys.ContainsKey(vk))
                _passedThroughKeys[vk] = now;
            else if (!down)
                _passedThroughKeys.Remove(vk);
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
                if (_passedThroughKeys.ContainsKey(vk))
                    _passedThroughKeys[vk] = now;
                return false;
            }
            _captureKeysDown.Remove(vk);
            _passedThroughKeys.Remove(vk);
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
            if (down) _passedThroughKeys[vk] = now;
            return swallowOwnedUp;
        }

        if (IsLongPressSpaceBinding(bindings.Hold)
            && vk == VirtualKeyCatalog.Space
            && down
            && _modifiers == Modifier.None)
        {
            _beforeLongPressSpaceAdmission();
            return _spaceHold.Process(down);
        }

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
        Func<bool>? normalTriggersEnabled = null,
        Action? beforeLongPressSpaceAdmission = null)
    {
        _bindings = new HotkeyBindings(hold, toggle, cancel);
        _log = log;
        _cancelEnabled = cancelEnabled ?? (() => true);
        _now = timeProvider ?? (() => DateTimeOffset.UtcNow);
        _keyPhysicallyDown = keyPhysicallyDown ?? DefaultKeyPhysicallyDown;
        _normalTriggersEnabled = normalTriggersEnabled ?? (() => true);
        _beforeLongPressSpaceAdmission = beforeLongPressSpaceAdmission ?? (() => { });
        _spaceHold = new LongPressSpaceStateMachine(
            spaceTimerScheduler ?? new SystemLongPressTimerScheduler(),
            kind => _events.Writer.TryWrite(new HotkeyEvent(kind, _now())),
            isSpacePhysicallyDown: () => _keyPhysicallyDown(VirtualKeyCatalog.Space),
            canStartHold: CanStartLongPressSpace);
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

    /// <summary>
    /// Re-reads every global admission condition from its published state.
    /// Lifecycle transitions publish first and cancel second, so a callback
    /// that observed stale local state either fails this check or is cancelled
    /// before the transition returns.
    /// </summary>
    private bool CanStartLongPressSpace()
        => NormalTriggersEnabled()
            && IsLongPressSpaceBinding(Volatile.Read(ref _bindings).Hold)
            && Volatile.Read(ref _suspendRequested) == 0
            && Volatile.Read(ref _rawCapture) is null;

    /// <summary>Atomically replaces the active hold and toggle chords.</summary>
    public void UpdateChords(HotkeyChord hold, HotkeyChord toggle)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ArgumentNullException.ThrowIfNull(toggle);

        var current = Volatile.Read(ref _bindings);
        Volatile.Write(ref _bindings, new HotkeyBindings(hold, toggle, current.Cancel));
        if (IsLongPressSpaceBinding(current.Hold) && !IsLongPressSpaceBinding(hold))
            _spaceHold.Cancel();
        _log.LogInformation("Hotkeys updated: hold={Hold}, toggle={Toggle}", hold, toggle);
    }

    /// <summary>
    /// Lets the settings UI receive key events without the global hook firing
    /// or swallowing the chord currently being captured.
    /// </summary>
    public void SetSuspended(bool suspended)
    {
        Volatile.Write(ref _suspendRequested, suspended ? 1 : 0);
        if (suspended) _spaceHold.Cancel();
        _log.LogDebug("Hotkey hook {State} for chord capture", suspended ? "suspended" : "resumed");
    }

    /// <summary>
    /// Acquires exclusive focus-independent raw keyboard capture. Captured keys
    /// pass through to Windows and bypass configured trigger processing.
    /// </summary>
    public IDisposable BeginRawCapture(Action<RawKeyTransition> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        RawCaptureRegistration registration;
        lock (_captureGate)
        {
            if (_rawCapture is not null)
                throw new InvalidOperationException("A raw hotkey capture lease is already active.");
            registration = new RawCaptureRegistration(sink);
            Volatile.Write(ref _rawCapture, registration);
        }

        // Do not hold _captureGate while taking the Space state-machine lock.
        // The registration cannot be unpublished before the lease is returned,
        // so admission sees raw capture enabled throughout cancellation without
        // introducing a capture-lock -> Space-lock ordering requirement.
        _spaceHold.Cancel();
        _log.LogDebug("Raw hotkey capture acquired");
        return new RawCaptureLease(this, registration);
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
        RegisterPowerNotifications();
        StartHeartbeat();
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
            // Thread messages carry no window, so handle ours here rather than
            // dispatching. Running the reinstall on THIS thread is the point:
            // it is the only thread that touches the hook handle and the
            // per-chord tracking dictionaries, so no locking is introduced.
            if (msg.Message == WM_WINPEPPER_REINSTALL_HOOK)
            {
                ReinstallOnHookThread();
                continue;
            }
            TranslateMessage(msg);
            DispatchMessageW(msg);
        }

        UnhookWindowsHookEx(_hookHandle);
        _events.Writer.TryComplete();
        _log.LogInformation("Hotkey hook thread exiting");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        Volatile.Write(ref _lastHookCallbackTick, Environment.TickCount64);
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

    /// <summary>
    /// Ask the hook thread to reinstall WH_KEYBOARD_LL. Safe to call from any
    /// thread: the unhook/hook and the tracking-state reset run ON the hook
    /// thread via its message loop, so they never race the hook callback.
    /// </summary>
    public void RequestHookReinstall()
    {
        Volatile.Write(ref _reinstallRequestedTick, Environment.TickCount64);
        if (_hookThread is null)
        {
            // Never started (unit tests, or before Start): there is no hook to
            // reinstall and no other thread touching tracking state.
            ReinstallOnHookThread();
            return;
        }
        if (!PostThreadMessageW(_hookThreadId, WM_WINPEPPER_REINSTALL_HOOK, IntPtr.Zero, IntPtr.Zero))
            // Known gap (recorded above): a lost post is never retried.
            _log.LogWarning("Failed to post hook reinstall to the hook thread: 0x{Err:X}",
                Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// Runs on the hook thread. Resets per-chord tracking, then swaps the hook.
    /// Every branch logs: these lines are what turns the next field incident
    /// into evidence instead of guesswork.
    /// </summary>
    private void ReinstallOnHookThread()
    {
        _log.LogInformation("System resumed; reinstalling keyboard hook");
        // A post that executes late points at a busy-but-alive hook thread; a
        // post that NEVER executes (no line at all) is the wedged-thread
        // signature.
        _log.LogInformation("reinstall executed {Ms} ms after resume callback",
            Environment.TickCount64 - Volatile.Read(ref _reinstallRequestedTick));
        ResetTrackingState();
        if (_hookThread is null || _callback is null) return; // no live hook to reinstall

        if (_hookHandle != IntPtr.Zero)
        {
            // Do NOT discard the result: false means the OS had ALREADY removed
            // the hook (the case we are healing); true means the hook was still
            // installed and this resume-reinstall was precautionary.
            var unhooked = UnhookWindowsHookEx(_hookHandle);
            _log.LogInformation(
                "Stale hook unhook returned {Result} (false = OS had already removed the hook)",
                unhooked);
            _hookHandle = IntPtr.Zero;
        }
        _hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, _callback, GetModuleHandleW(null), 0);
        if (_hookHandle == IntPtr.Zero)
            // Known gap (recorded above): no retry here - dead until next resume.
            _log.LogWarning("Keyboard hook reinstall failed: 0x{Err:X}", Marshal.GetLastWin32Error());
        else
            _log.LogInformation("Keyboard hook reinstalled (thread {ThreadId})",
                Environment.CurrentManagedThreadId);
    }

    /// <summary>
    /// Drops every per-chord tracking entry so a chord that was half-tracked
    /// across a suspend cannot fire (or stay swallowed) after resume: the
    /// key-ups that would have closed it were never delivered. Deliberately
    /// does NOT touch the raw-capture lease or the suspend-for-capture flag - a
    /// settings chord recording in progress stays in progress.
    /// <para>
    /// Clearing <c>_holding</c> is NOT enough on its own: <c>TryProcessKey</c>
    /// only emits <c>HoldUp</c> when <c>_holding</c> is true (`HotkeyHook.cs:295`),
    /// so a silent clear would swallow the terminating event of a dictation
    /// that was in flight across the suspend - `PipelineHost.HandleHotkey`
    /// would never run the HoldUp branch (`PipelineHost.cs:394-396`), leaving
    /// `SessionEngine` stuck in `Recording` FOREVER: the mic stays open, the
    /// unbounded session buffer keeps growing (`WarmCaptureBuffer.Ingest`), and
    /// every later HoldDown is dropped by the `State != Idle` guard
    /// (`PipelineHost.cs:376`) - i.e. the hotkey looks dead again, the exact
    /// symptom this task exists to fix. So we EMIT the terminating HoldUp
    /// ourselves and then forget the hold. The subsequent physical key-up (if
    /// one is ever delivered) finds `_holding == false` and produces nothing,
    /// so the session sees exactly one HoldUp. If no session was running the
    /// event is a harmless no-op: the HoldUp branch returns immediately unless
    /// the engine is `Recording`.
    /// </para>
    /// </summary>
    internal void ResetTrackingState()
    {
        _swallowedKeys.Clear();
        _passedThroughKeys.Clear();
        _captureKeysDown.Clear();
        _observedCancelKeys.Clear();
        _modifiers = Modifier.None;
        _spaceHold.Cancel();
        if (_holding)
        {
            _holding = false;
            // Unbounded channel with SingleWriter:false - safe to write from
            // the hook thread (the normal path) or the caller's thread (the
            // never-started path in RequestHookReinstall).
            _log.LogInformation(
                "Reinstall interrupted an in-flight hold; emitting the terminating HoldUp so the dictation ends");
            _events.Writer.TryWrite(new HotkeyEvent(HotkeyEventKind.HoldUp, DateTimeOffset.UtcNow));
        }
    }

    private void RegisterPowerNotifications()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            _powerCallback = OnPowerNotification;
            var parameters = new PowerNotificationNative.DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
            {
                Callback = Marshal.GetFunctionPointerForDelegate(_powerCallback),
                Context = IntPtr.Zero,
            };
            var rc = PowerNotificationNative.PowerRegisterSuspendResumeNotification(
                PowerNotificationNative.DEVICE_NOTIFY_CALLBACK, ref parameters, out var handle);
            if (rc == PowerNotificationNative.ERROR_SUCCESS)
            {
                _powerRegistration = handle;
                _log.LogInformation("Registered for suspend/resume notifications (handle 0x{Handle:X})",
                    handle.ToInt64());
            }
            else
            {
                _powerCallback = null;
                _log.LogWarning("PowerRegisterSuspendResumeNotification failed: 0x{Err:X}", rc);
            }
        }
        catch (Exception ex)
        {
            _powerCallback = null;
            _log.LogWarning(ex, "suspend/resume notifications unavailable; hotkeys will not self-heal after resume");
        }
    }

    /// <summary>
    /// Runs on a system callback thread: decide, post, return. Never block here.
    /// The Debug line lets the smoke distinguish "registered but nothing
    /// delivered" from "resume classified wrong" (raw PBT_* type included).
    /// </summary>
    private uint OnPowerNotification(IntPtr context, uint type, IntPtr setting)
    {
        _log.LogDebug("Power notification: type=0x{Type:X}", type);
        if (PowerResumeDecision.IsResume(type)) RequestHookReinstall();
        return PowerNotificationNative.ERROR_SUCCESS;
    }

    private void UnregisterPowerNotifications()
    {
        var handle = _powerRegistration;
        _powerRegistration = IntPtr.Zero;
        if (handle != IntPtr.Zero && OperatingSystem.IsWindows())
        {
            try { _ = PowerNotificationNative.PowerUnregisterSuspendResumeNotification(handle); }
            catch (Exception ex) { _log.LogDebug(ex, "PowerUnregisterSuspendResumeNotification failed"); }
        }
        // Only after the OS can no longer call it.
        _powerCallback = null;
    }

    /// <summary>
    /// TELEMETRY ONLY - an unconditional, non-judging evidence line, NOT a
    /// health verdict and NOT a reinstall trigger. Every 30 s it records two
    /// ages: how long since the OS last called our low-level keyboard hook,
    /// and how long since the system last saw ANY user input. Post-incident,
    /// this gives a timeline ("the hook went quiet at T, input continued
    /// past T") that today's logs cannot provide at all.
    ///
    /// WHY IT DOES NOT DECIDE: there is no Win32 API for "time of last
    /// KEYBOARD input". GetLastInputInfo is system-wide and is updated by
    /// MOUSE input too, so "input recent AND hook silent" is satisfied by
    /// ordinary mouse-only use (reading, scrolling) on a perfectly healthy
    /// hook. Emitting a WRN on that conjunction would fire routinely on
    /// healthy systems and drown the content-free log the incident response
    /// depends on. Two ages at DEBUG are honest; a warning would not be.
    ///
    /// Consequently this is NOT the detector for a silently-removed hook -
    /// the Task 9 R14-2 gate is a direct functional check (press the hotkey;
    /// does a dictation start?), and these lines are corroborating timeline
    /// evidence for it. Promoting this to a trigger requires a genuinely
    /// keyboard-specific liveness signal first (e.g. a WM_INPUT raw-input
    /// keyboard sink independent of the hook), which is out of scope here.
    /// </summary>
    private void StartHeartbeat()
    {
        if (!OperatingSystem.IsWindows()) return;
        _heartbeatTimer = new Timer(_ =>
        {
            try
            {
                var sinceCallbackMs = Environment.TickCount64 - Volatile.Read(ref _lastHookCallbackTick);
                long? sinceInputMs = null;
                var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
                if (GetLastInputInfo(ref info))
                {
                    // GetLastInputInfo reports 32-bit ticks; compare in 32-bit space.
                    var delta = unchecked(Environment.TickCount - (int)info.dwTime);
                    if (delta >= 0) sinceInputMs = delta;
                }
                // DEBUG, unconditional, no verdict: system-wide input includes
                // MOUSE, so these two ages diverging is NORMAL, not a fault.
                _log.LogDebug(
                    "Hook heartbeat: lastCallbackAgeMs={CallbackAge} lastAnyInputAgeMs={InputAge} (input age is system-wide and includes mouse)",
                    sinceCallbackMs, sinceInputMs?.ToString() ?? "unknown");
            }
            catch { /* telemetry must never take the app down */ }
        }, null, HeartbeatPeriod, HeartbeatPeriod);
    }

    private static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(30);

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
        UnregisterPowerNotifications(); // stop resume callbacks before teardown
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        _spaceHold.Dispose();
        lock (_captureGate) Volatile.Write(ref _rawCapture, null);
        if (_hookThread is null) return;
        PostThreadMessageW(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _hookThread.Join(TimeSpan.FromSeconds(2));
        _hookThread = null;
    }
}
