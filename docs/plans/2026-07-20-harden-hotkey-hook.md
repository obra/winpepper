# Harden Global Keyboard Hook Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Make Winpepper's global low-level keyboard hook (`WH_KEYBOARD_LL`)
self-healing so a missed key-up, a wedged capture-drain, a leaked suspend, or
an unsafe hand-edited chord binding can NEVER leave a common key swallowed
system-wide or the hook silently dead.

**Architecture:** `HotkeyHook.TryProcessKey` is the pure-managed per-key
decision point (swallow vs pass-through). Today it tracks swallowed trigger
keys and capture keys in in-memory sets that are only cleared on the matching
observed key-up. Windows silently drops low-level hook callbacks that exceed
`LowLevelHooksTimeout` (~300 ms), so a lost key-up strands those entries
forever. This plan makes every such entry **self-heal**: an entry is dropped
once the physical key is no longer held (`GetAsyncKeyState`) or it outlives a
short bounded window (timestamp expiry). Both signals are injected as
constructor seams (`Func<DateTimeOffset>` clock, `Func<int,bool>` physical
probe) so the healing is fully testable on Linux without P/Invoke. A new
pure-managed `RecorderSuspendCoordinator` guarantees suspend is released on
recorder teardown, and a new `HotkeyChord` validation policy rejects unsafe
modifier-less trigger bindings both in the settings UI and when loading a
hand-edited settings file.

**Tech Stack:** C# / .NET 9 (`net9.0` + `net9.0-windows10.0.19041.0`
multi-target), xUnit v3 (Microsoft Testing Platform), Shouldly assertions.
All hardened logic lives in `Winpepper.Platform` / `Winpepper.Core`
pure-managed code exercised through `TryProcessKey`, `RecorderSuspendCoordinator`,
and `HotkeyChord` static methods, so tests run cross-platform on Linux.

## Global Constraints

- **.NET SDK prerequisite (verify before Task 1):** a .NET SDK satisfying
  `global.json` (`sdk 9.0.100`, `rollForward: latestFeature`) must be on `PATH`.
  Confirm with `dotnet --version`. If absent (a known state of this bare Linux
  worktree), provision the SDK first exactly as the prior plan
  `docs/plans/2026-07-20-modifier-key-passthrough.md` describes (local SDK
  provisioning). All hardened logic is pure managed (`net9.0`, no P/Invoke on
  the test path), so once the SDK is present the tests run on Linux.
- **Test execution convention (VSTest host crashes on this machine — do NOT use
  `dotnet test`).** Build the test project for `net9.0` and run the built dll
  directly through the xUnit v3 in-process runner (Microsoft Testing Platform)
  via `dotnet exec`. Use these exact forms everywhere this plan says "build &
  run":
  - Build: `dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Debug -f net9.0`
  - Test dll: `tests/Winpepper.Platform.Tests/bin/Debug/net9.0/Winpepper.Platform.Tests.dll`
  - Run one class: `dotnet exec <test-dll> --filter-class "<FullyQualifiedClassName>"`
  - Run one method: `dotnet exec <test-dll> --filter-method "<FullyQualifiedClassName>.<Method>"`
  - Run whole non-Windows suite: `dotnet exec <test-dll> --filter-not-trait "Platform=Windows"`
  You MUST rebuild before each run — `dotnet exec` runs the last-built dll.
- **C# TDD reality:** a test that references a not-yet-added member/parameter
  fails at COMPILE time. That compile failure IS the RED state. Where a RED
  step below says "build FAILS with CSxxxx", that is the expected failing test.
- Target framework for logic tests on Linux: `net9.0`. The
  `net9.0-windows10.0.19041.0` target and `[Trait("Platform","Windows")]` tests
  are Windows-only and excluded via `--filter-not-trait "Platform=Windows"`.
- **Preserve every existing pass-through invariant (must hold for every task):**
  - Modifier keys (Ctrl/Shift/Alt/Win, left or right) are NEVER swallowed
    (`IsModifierKey` guards at `HotkeyHook.cs:103` and `:113`).
  - The Cancel/Esc key is NEVER swallowed (Cancel branch returns `false`),
    covered by `CancelChord_PlainEsc_EmitsCancelWithoutSwallowing` — do NOT
    regress it.
  - Key-up symmetry: an up is swallowed only when its matching down was
    swallowed.
- **Healing invariants introduced by this plan:**
  - A swallowed entry must not outlive the actual physical key press by more
    than a short bounded window (`StaleKeyTimeout`, 1500 ms).
  - A key-down for a key that is NOT physically held must never be swallowed by
    the stale-entry guard.
  - Drain mode (`_suspendRequested || _captureKeysDown.Count != 0`) must recover
    on the next key event after a lost capture key-up.
- Commit style: focused, atomic commits with `feat:`/`test:`/`docs:` prefixes
  and the standard Amplifier co-author trailer (shown in each commit step).
- `README.md` is the only end-user markdown doc; this plan under `docs/plans/`
  is a working/agent doc.

---

## Root Cause (read before starting)

In `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs`:

1. **Stuck `_swallowedKeys`.** A non-modifier trigger key (default toggle
   `Ctrl+Shift+Space` → `Space`) is added to `_swallowedKeys` on activation
   (`:104`, `:114`) and removed only on its observed key-up (`:125`, and the
   suspended path `:73`). The autorepeat guard (`:88`
   `if (_swallowedKeys.Contains(vk)) return true;`) then swallows that key
   forever if the key-up is lost. No timeout, no self-heal.
2. **Permanent drain wedge.** `_captureKeysDown` entries (keys pressed during
   suspend/capture, added at `:65`) are removed only on observed key-up (`:68`).
   A lost key-up keeps `_captureKeysDown.Count != 0`, wedging the hook in drain
   mode (`:58`) and silently killing all hotkeys until restart.
3. **Suspend leak.** `_suspendRequested` (set via `SetSuspended`, `:174`) stays
   set if the recorder UI (`HotkeyRecorderBox`, wired through
   `PipelineHost.SetHotkeyCaptureActive` → `HotkeyHook.SetSuspended`) is torn
   down without raising Cancel/Commit/LostFocus — global hotkeys silently dead.
4. **Unsafe bindings accepted.** `HotkeyChord.Parse` (`HotkeyChord.cs:56-87`)
   accepts bare common keys (Esc, Tab, Enter, Space, letters, digits, arrows)
   with no modifier and nothing rejects them, so a hand-edited settings file can
   bind `Esc` as a toggle/hold trigger — swallowed globally by design at
   `:104`/`:114`, recreating the "Esc broken system-wide" failure.

**Fix strategy:** introduce two injected seams (clock + physical-key probe),
convert `_swallowedKeys` and `_captureKeysDown` to `Dictionary<int,DateTimeOffset>`
(vk → last-observed timestamp), and drop any entry whose key is not physically
held or has expired. Add `RecorderSuspendCoordinator` to guarantee suspend
release on teardown, and add `HotkeyChord.ValidateTriggerBinding` /
`ParseTriggerOrDefault` policy enforced in the UI validator and the settings
load path.

---

## File Structure

- `src/Winpepper.Platform/Hotkeys/KeyboardHookNative.cs` — **modified.** Add the
  `GetAsyncKeyState` P/Invoke (used only in production on Windows).
- `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs` — **modified.** Inject clock +
  physical-key seams; convert `_swallowedKeys` (Task 1) and `_captureKeysDown`
  (Task 2) to timestamped dictionaries; add `IsKeyEntryLive` / `PruneStaleKeys`;
  self-heal the swallow guard and the capture drain.
- `src/Winpepper.Platform/Hotkeys/RecorderSuspendCoordinator.cs` — **created.**
  Pure-managed guarantee that suspend is released exactly once on recorder
  teardown (Task 3).
- `src/Winpepper.App/Views/Controls/HotkeyRecorderBox.xaml.cs` — **modified,
  Windows-only.** Route recording state through the coordinator and call
  `Teardown()` on `Unloaded` (Task 3). Not built/tested on Linux; its logic is
  the tested coordinator.
- `src/Winpepper.Platform/Hotkeys/HotkeyChord.cs` — **modified.** Add
  `ValidateTriggerBinding` and `ParseTriggerOrDefault` validation policy (Task 4).
- `src/Winpepper.Platform/Hotkeys/PlatformHotkeyValidator.cs` — **modified.**
  Enforce the trigger policy in the settings UI validator (Task 4).
- `src/Winpepper.App/Hosting/AppShell.cs` — **modified, Windows-only.** Load
  hold/toggle chords through `ParseTriggerOrDefault` so an unsafe hand-edited
  value falls back to the default with a logged warning (Task 4). Not built on
  Linux; its logic is the tested `ParseTriggerOrDefault`.
- `tests/Winpepper.Platform.Tests/Hotkeys/SwallowSelfHealTests.cs` — **created**
  (Task 1).
- `tests/Winpepper.Platform.Tests/Hotkeys/CaptureDrainSelfHealTests.cs` —
  **created** (Task 2).
- `tests/Winpepper.Platform.Tests/Hotkeys/RecorderSuspendCoordinatorTests.cs` —
  **created** (Task 3).
- `tests/Winpepper.Platform.Tests/Hotkeys/HotkeyChordValidationTests.cs` —
  **created** (Task 4).

---

## Scope Check

This is one cohesive subsystem (the keyboard hook's safety/liveness). It does
not warrant splitting into multiple plans. The four tasks map 1:1 to the four
enumerated defects and each ends with an independently testable deliverable.
Task 1 also introduces the shared seams and prune helper that Task 2 extends.

**Intentional scope boundary (not a coverage gap):** healing drops stale
*swallow/capture* entries. It does not synthesize a `HoldUp` for a lost hold
key-up (the `_holding` flag clears on the next real key-up of the hold trigger,
via the existing `:126` check). The four defects are about a key being
swallowed system-wide or the hook wedged — both fully covered. No new event
semantics are invented.

---

### Task 1: Self-heal stuck `_swallowedKeys` (defect 1)

**Files:**
- Modify: `src/Winpepper.Platform/Hotkeys/KeyboardHookNative.cs` (add
  `GetAsyncKeyState` P/Invoke after the existing `PostThreadMessageW` import,
  near line 63)
- Modify: `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs` (fields near
  `:29-33`; constructor `:134-144`; `TryProcessKey` `:50-132`; add helpers near
  `ModifierForVirtualKey` `:269`)
- Create: `tests/Winpepper.Platform.Tests/Hotkeys/SwallowSelfHealTests.cs`

**Interfaces:**
- Consumes (already exists, do not redefine):
  - `bool HotkeyHook.TryProcessKey(int vk, bool down, out HotkeyEvent? evt)` —
    returns `true` to swallow, `false` to pass through.
  - `static bool HotkeyHook.IsModifierKey(int vk)` and
    `static Modifier HotkeyHook.ModifierForVirtualKey(int vk)` (private).
  - `HotkeyChord.Parse(string)`, `HotkeyEventKind.{HoldDown,HoldUp,Toggle,Cancel}`.
  - VK constants from `KeyboardHookNative` (`using static`): `VK_LCONTROL`,
    `VK_RCONTROL`, `VK_LSHIFT`, `VK_RSHIFT`, `VK_LMENU`, `VK_RMENU`, `VK_LWIN`,
    `VK_RWIN`.
- Produces (relied on by later tasks):
  - New constructor overload:
    `HotkeyHook(HotkeyChord hold, HotkeyChord toggle, HotkeyChord cancel,
    ILogger<HotkeyHook> log, Func<bool>? cancelEnabled = null,
    Func<DateTimeOffset>? timeProvider = null,
    Func<int,bool>? keyPhysicallyDown = null)` — new optional params appended;
    all existing callers keep compiling.
  - `_swallowedKeys` is now `Dictionary<int,DateTimeOffset>`.
  - Private helpers `bool IsKeyEntryLive(int vk, DateTimeOffset since, DateTimeOffset now)`
    and `void PruneStaleKeys(DateTimeOffset now, int exceptVk)` (Task 2 extends
    `PruneStaleKeys` to also prune `_captureKeysDown`).
  - `static readonly TimeSpan HotkeyHook.StaleKeyTimeout` = 1500 ms.

- [ ] **Step 1: Write the failing self-heal tests**

Create `tests/Winpepper.Platform.Tests/Hotkeys/SwallowSelfHealTests.cs` with
exactly this content:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;
using static Winpepper.Platform.Hotkeys.KeyboardHookNative;

namespace Winpepper.Platform.Tests.Hotkeys;

/// <summary>
/// End-user story: Windows can silently drop the key-up for a swallowed hotkey
/// trigger (heavy ASR work exceeds LowLevelHooksTimeout). A dropped key-up must
/// NEVER leave that common key swallowed system-wide. The stale entry must
/// self-heal - either because the physical key is no longer held
/// (GetAsyncKeyState) or because it outlived the bounded StaleKeyTimeout - so a
/// fresh press of that key reaches the app.
/// </summary>
public class SwallowSelfHealTests
{
    // Space is the non-modifier trigger of the default Ctrl+Shift+Space toggle.
    private const int Space = 0x20;
    private const int A = 0x41;

    private static HotkeyHook NewHook(
        Func<DateTimeOffset> now,
        Func<int, bool> physicallyDown,
        string hold = "RightCtrl+RightShift",
        string toggle = "Ctrl+Shift+Space",
        string cancel = "Esc")
        => new(HotkeyChord.Parse(hold), HotkeyChord.Parse(toggle),
               HotkeyChord.Parse(cancel), new NullLogger<HotkeyHook>(),
               cancelEnabled: null, timeProvider: now, keyPhysicallyDown: physicallyDown);

    [Fact]
    public void SwallowedTrigger_LostKeyUp_PhysicalRelease_FreshPressPassesThrough()
    {
        var down = new HashSet<int>();
        var clock = DateTimeOffset.UtcNow;
        var hook = NewHook(now: () => clock, physicallyDown: down.Contains);

        // Press the toggle chord. Space (the trigger) is swallowed.
        down.Add(VK_LCONTROL); hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        down.Add(VK_LSHIFT);   hook.TryProcessKey(VK_LSHIFT, down: true, out _).ShouldBeFalse();
        down.Add(Space);       hook.TryProcessKey(Space, down: true, out var toggle).ShouldBeTrue();
        toggle!.Kind.ShouldBe(HotkeyEventKind.Toggle);

        // Space's key-up is NEVER delivered. The user physically releases Space
        // and both modifiers (their ups DO arrive - modifiers pass through).
        down.Remove(Space);
        down.Remove(VK_LSHIFT); hook.TryProcessKey(VK_LSHIFT, down: false, out _).ShouldBeFalse();
        down.Remove(VK_LCONTROL); hook.TryProcessKey(VK_LCONTROL, down: false, out _).ShouldBeFalse();

        // The modifier-up events already ran the stale sweep and healed Space
        // (physically up). A fresh, standalone Space press must reach the app.
        down.Add(Space);
        hook.TryProcessKey(Space, down: true, out var fresh).ShouldBeFalse();
        fresh.ShouldBeNull();
    }

    [Fact]
    public void SwallowedTrigger_LostKeyUp_HealedByExpiry_FreshPressPassesThrough()
    {
        var clock = DateTimeOffset.UtcNow;
        // Physical probe always reports DOWN, so healing here must come from the
        // bounded StaleKeyTimeout expiry alone.
        var hook = NewHook(now: () => clock, physicallyDown: _ => true);

        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(Space, down: true, out var toggle).ShouldBeTrue();
        toggle!.Kind.ShouldBe(HotkeyEventKind.Toggle);

        // Space up lost; modifiers released so a later Space is a bare press.
        hook.TryProcessKey(VK_LSHIFT, down: false, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LCONTROL, down: false, out _).ShouldBeFalse();

        // Advance past the bounded stale window. The stale Space entry expires.
        clock = clock.AddSeconds(2);

        hook.TryProcessKey(Space, down: true, out var fresh).ShouldBeFalse();
        fresh.ShouldBeNull();
    }

    [Fact]
    public void HeldTrigger_Autorepeat_StillSwallowed_WhilePhysicallyDown()
    {
        var down = new HashSet<int>();
        var clock = DateTimeOffset.UtcNow;
        var hook = NewHook(now: () => clock, physicallyDown: down.Contains);

        down.Add(VK_LCONTROL); hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        down.Add(VK_LSHIFT);   hook.TryProcessKey(VK_LSHIFT, down: true, out _).ShouldBeFalse();
        down.Add(Space);       hook.TryProcessKey(Space, down: true, out var first).ShouldBeTrue();
        first!.Kind.ShouldBe(HotkeyEventKind.Toggle);

        // Genuine autorepeat: physically held, well within StaleKeyTimeout. Keep
        // swallowing and never re-fire Toggle.
        for (var i = 0; i < 3; i++)
        {
            clock = clock.AddMilliseconds(40);
            hook.TryProcessKey(Space, down: true, out var repeat).ShouldBeTrue();
            repeat.ShouldBeNull();
        }

        down.Remove(Space);
        hook.TryProcessKey(Space, down: false, out _).ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Build & run the new tests to verify they FAIL (compile RED)**

Run:
```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Debug -f net9.0
```
Expected: **build FAILS** with `CS1739`/`CS1503` on the `timeProvider:` /
`keyPhysicallyDown:` named arguments — the `HotkeyHook` constructor does not yet
accept them. This compile failure is the RED state for all three tests.

- [ ] **Step 3: Add the `GetAsyncKeyState` P/Invoke**

In `src/Winpepper.Platform/Hotkeys/KeyboardHookNative.cs`, add immediately after
the existing `PostThreadMessageW` import (after line 63, before
`public const uint WM_QUIT`):

```csharp
    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);
```

- [ ] **Step 4: Add the clock/probe fields, timeout, and convert `_swallowedKeys`**

In `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs`, replace this field block
(currently `:27-33`):

```csharp
    private Modifier _modifiers;
    private bool _holding;
    private readonly HashSet<int> _swallowedKeys = new();
    private readonly HashSet<int> _observedCancelKeys = new();
    private readonly HashSet<int> _captureKeysDown = new();
    private int _suspendRequested;
    private readonly ManualResetEventSlim _ready = new(initialState: false);
```

with:

```csharp
    private Modifier _modifiers;
    private bool _holding;
    // vk -> timestamp the swallow was last observed. Entries self-heal (drop)
    // when the physical key is no longer held or the entry outlives
    // StaleKeyTimeout, so a lost key-up can never swallow a key forever.
    private readonly Dictionary<int, DateTimeOffset> _swallowedKeys = new();
    private readonly HashSet<int> _observedCancelKeys = new();
    private readonly HashSet<int> _captureKeysDown = new();
    private int _suspendRequested;
    private readonly ManualResetEventSlim _ready = new(initialState: false);

    // Longer than Windows' max autorepeat initial delay (~1s) and far longer
    // than LowLevelHooksTimeout (~300ms), so a genuinely held key (refreshed by
    // autorepeat) is never falsely healed, but a lost key-up cannot strand an
    // entry for more than this bounded window.
    private static readonly TimeSpan StaleKeyTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly Func<DateTimeOffset> _now;
    private readonly Func<int, bool> _keyPhysicallyDown;
```

- [ ] **Step 5: Add the constructor seams**

In `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs`, replace the constructor
(currently `:134-144`):

```csharp
    public HotkeyHook(
        HotkeyChord hold,
        HotkeyChord toggle,
        HotkeyChord cancel,
        ILogger<HotkeyHook> log,
        Func<bool>? cancelEnabled = null)
    {
        _bindings = new HotkeyBindings(hold, toggle, cancel);
        _log = log;
        _cancelEnabled = cancelEnabled ?? (() => true);
    }
```

with:

```csharp
    public HotkeyHook(
        HotkeyChord hold,
        HotkeyChord toggle,
        HotkeyChord cancel,
        ILogger<HotkeyHook> log,
        Func<bool>? cancelEnabled = null,
        Func<DateTimeOffset>? timeProvider = null,
        Func<int, bool>? keyPhysicallyDown = null)
    {
        _bindings = new HotkeyBindings(hold, toggle, cancel);
        _log = log;
        _cancelEnabled = cancelEnabled ?? (() => true);
        _now = timeProvider ?? (() => DateTimeOffset.UtcNow);
        _keyPhysicallyDown = keyPhysicallyDown ?? DefaultKeyPhysicallyDown;
    }

    // Real physical key-state probe. Guarded so it is only P/Invoked on Windows;
    // on other platforms (Linux test host) production never installs the hook,
    // and unit tests inject their own probe, so returning true here is inert.
    private static bool DefaultKeyPhysicallyDown(int vk)
        => !OperatingSystem.IsWindows() || (GetAsyncKeyState(vk) & 0x8000) != 0;
```

- [ ] **Step 6: Add the liveness + prune helpers**

In `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs`, add these methods immediately
above `private static bool IsModifierKey(int vk)` (currently `:267`):

```csharp
    /// <summary>
    /// A tracked key entry is "live" only while the physical key is still held
    /// AND the entry has not outlived <see cref="StaleKeyTimeout"/>. Anything
    /// else is stale and must be dropped so a lost key-up can never strand it.
    /// </summary>
    private bool IsKeyEntryLive(int vk, DateTimeOffset since, DateTimeOffset now)
        => _keyPhysicallyDown(vk) && (now - since) <= StaleKeyTimeout;

    /// <summary>
    /// Drops stale entries from the tracked key dictionaries, healing keys whose
    /// key-up was lost. <paramref name="exceptVk"/> is the key of the current
    /// event, which the normal down/up logic handles explicitly (so happy-path
    /// swallow/up symmetry is preserved).
    /// </summary>
    private void PruneStaleKeys(DateTimeOffset now, int exceptVk)
    {
        PruneStale(_swallowedKeys, now, exceptVk);
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
```

- [ ] **Step 7: Heal the swallow guard and timestamp the swallow sites**

In `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs`, replace the top of
`TryProcessKey` (currently `:50-58`):

```csharp
    public bool TryProcessKey(int vk, bool down, out HotkeyEvent? evt)
    {
        evt = null;
        var modifiersBeforeEvent = _modifiers;
        UpdateModifierState(vk, down);
        var bindings = Volatile.Read(ref _bindings);

        var suspendRequested = Volatile.Read(ref _suspendRequested) != 0;
        if (suspendRequested || _captureKeysDown.Count != 0)
```

with:

```csharp
    public bool TryProcessKey(int vk, bool down, out HotkeyEvent? evt)
    {
        evt = null;
        var now = _now();
        var modifiersBeforeEvent = _modifiers;
        UpdateModifierState(vk, down);
        var bindings = Volatile.Read(ref _bindings);

        // Self-heal: drop any tracked entry whose physical key is no longer held
        // or that outlived StaleKeyTimeout, so a lost key-up can never leave a
        // key swallowed or the hook wedged. The current key is handled below.
        PruneStaleKeys(now, exceptVk: vk);

        var suspendRequested = Volatile.Read(ref _suspendRequested) != 0;
        if (suspendRequested || _captureKeysDown.Count != 0)
```

Then replace the autorepeat guard (currently `:88`):

```csharp
            if (_swallowedKeys.Contains(vk)) return true;
```

with:

```csharp
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
```

Then replace the Toggle swallow add (currently `:104`):

```csharp
                if (IsModifierKey(vk)) return false;
                _swallowedKeys.Add(vk);
                return true;
```

with:

```csharp
                if (IsModifierKey(vk)) return false;
                _swallowedKeys[vk] = now;
                return true;
```

Then replace the Hold swallow add (currently `:114`):

```csharp
                if (IsModifierKey(vk)) return false;
                _swallowedKeys.Add(vk);
                return true;
```

with:

```csharp
                if (IsModifierKey(vk)) return false;
                _swallowedKeys[vk] = now;
                return true;
```

Note: the two `_swallowedKeys.Remove(vk)` calls (suspended path `:73` and the
key-up path `:125`) already work unchanged — `Dictionary<int,DateTimeOffset>.Remove`
returns `bool` exactly like the old `HashSet.Remove`.

- [ ] **Step 8: Build & run the new tests to verify they PASS (GREEN)**

Run:
```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Platform.Tests/bin/Debug/net9.0/Winpepper.Platform.Tests.dll \
  --filter-class "Winpepper.Platform.Tests.Hotkeys.SwallowSelfHealTests"
```
Expected: build succeeds; run reports **3 passed, 0 failed**.

- [ ] **Step 9: Run the full non-Windows Platform suite (no regressions)**

Run:
```bash
dotnet exec tests/Winpepper.Platform.Tests/bin/Debug/net9.0/Winpepper.Platform.Tests.dll \
  --filter-not-trait "Platform=Windows"
```
Expected: **0 failed.** Existing tests use the default probe (returns `true` on
Linux) and default clock, so entries are only removed on real key-up exactly as
before — `HotkeyHookLogicTests`, `ModifierPassthroughTests`, `HotkeyChordTests`,
`ChordRecorderTests`, `HotkeyConflictsTests` all stay green.

- [ ] **Step 10: Commit**

```bash
git add src/Winpepper.Platform/Hotkeys/KeyboardHookNative.cs \
        src/Winpepper.Platform/Hotkeys/HotkeyHook.cs \
        tests/Winpepper.Platform.Tests/Hotkeys/SwallowSelfHealTests.cs
git commit -m "$(cat <<'EOF'
feat: self-heal stuck swallowed keys in hotkey hook

A lost low-level key-up (LowLevelHooksTimeout drops callbacks under heavy ASR
load) could leave a swallowed trigger key hidden system-wide forever. Track
swallowed keys with timestamps and drop any entry whose physical key is no
longer held (GetAsyncKeyState) or that outlives a bounded window. Clock and
physical-key probe are injected seams so the healing is testable on Linux.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 2: Self-heal the capture-drain wedge (defect 2)

**Files:**
- Modify: `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs` (`_captureKeysDown`
  field `:31`; the capture-drain add site `:65`; `PruneStaleKeys` helper added
  in Task 1)
- Create: `tests/Winpepper.Platform.Tests/Hotkeys/CaptureDrainSelfHealTests.cs`

**Interfaces:**
- Consumes: everything Task 1 produced —
  `HotkeyHook(..., Func<DateTimeOffset>? timeProvider, Func<int,bool>? keyPhysicallyDown)`,
  `PruneStaleKeys(DateTimeOffset now, int exceptVk)`,
  `PruneStale(Dictionary<int,DateTimeOffset>, DateTimeOffset, int)`,
  `IsKeyEntryLive`, `StaleKeyTimeout`, plus `HotkeyHook.SetSuspended(bool)` and
  `HotkeyHook.TryProcessKey`.
- Produces: `_captureKeysDown` is now `Dictionary<int,DateTimeOffset>` and is
  pruned by `PruneStaleKeys`. No new public surface.

- [ ] **Step 1: Write the failing drain self-heal test**

Create `tests/Winpepper.Platform.Tests/Hotkeys/CaptureDrainSelfHealTests.cs`
with exactly this content:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;
using static Winpepper.Platform.Hotkeys.KeyboardHookNative;

namespace Winpepper.Platform.Tests.Hotkeys;

/// <summary>
/// End-user story: while the settings UI captures a chord, the hook drains
/// (passes through) every key until the captured keys are released. If a
/// captured key's key-up is dropped by Windows, drain mode must NOT wedge
/// forever and silently kill all hotkeys - it must recover on the next key
/// event so the user's hold/toggle hotkeys keep working.
/// </summary>
public class CaptureDrainSelfHealTests
{
    private const int A = 0x41;

    private static HotkeyHook NewHook(Func<DateTimeOffset> now, Func<int, bool> physicallyDown)
        => new(HotkeyChord.Parse("RightCtrl+RightShift"),
               HotkeyChord.Parse("Ctrl+Shift+Space"),
               HotkeyChord.Parse("Esc"), new NullLogger<HotkeyHook>(),
               cancelEnabled: null, timeProvider: now, keyPhysicallyDown: physicallyDown);

    [Fact]
    public void CaptureDrain_LostKeyUp_RecoversOnNextEvent_HotkeysResume()
    {
        var down = new HashSet<int>();
        var clock = DateTimeOffset.UtcNow;
        var hook = NewHook(now: () => clock, physicallyDown: down.Contains);

        hook.SetSuspended(true);

        // During capture the user holds LeftCtrl then A; both enter the drain set.
        down.Add(VK_LCONTROL); hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        down.Add(A);           hook.TryProcessKey(A, down: true, out _).ShouldBeFalse();

        // LeftCtrl is released normally (clears its modifier); A's key-up is LOST.
        down.Remove(VK_LCONTROL);
        hook.TryProcessKey(VK_LCONTROL, down: false, out _).ShouldBeFalse();
        hook.SetSuspended(false);
        down.Remove(A); // physically released, but no key-up event is delivered

        // Drain is wedged: _captureKeysDown still holds A. The hold chord must
        // still fire because the stale A entry self-heals on the next event.
        down.Add(VK_RCONTROL); hook.TryProcessKey(VK_RCONTROL, down: true, out _).ShouldBeFalse();
        down.Add(VK_RSHIFT);   hook.TryProcessKey(VK_RSHIFT, down: true, out var ev).ShouldBeFalse();
        ev!.Kind.ShouldBe(HotkeyEventKind.HoldDown);
    }
}
```

- [ ] **Step 2: Build & run to verify it FAILS (RED)**

Run:
```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Platform.Tests/bin/Debug/net9.0/Winpepper.Platform.Tests.dll \
  --filter-class "Winpepper.Platform.Tests.Hotkeys.CaptureDrainSelfHealTests"
```
Expected: build succeeds; run reports **1 failed**. `_captureKeysDown` still
holds `A` (its key-up was lost), so the drain branch keeps passing keys through
and the hold chord never fires — the final `ev!.Kind` assertion throws a
`NullReferenceException` / Shouldly failure because `ev` is `null`.

- [ ] **Step 3: Convert `_captureKeysDown` to a timestamped dictionary**

In `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs`, replace the field (currently
`:31`):

```csharp
    private readonly HashSet<int> _captureKeysDown = new();
```

with:

```csharp
    // vk -> timestamp last observed during capture. Same self-heal as
    // _swallowedKeys: a lost key-up must not wedge drain mode forever.
    private readonly Dictionary<int, DateTimeOffset> _captureKeysDown = new();
```

- [ ] **Step 4: Timestamp the capture add site**

In `TryProcessKey`, inside the drain branch, replace (currently `:63-67`):

```csharp
            if (down)
            {
                _captureKeysDown.Add(vk);
                return false;
            }
```

with:

```csharp
            if (down)
            {
                _captureKeysDown[vk] = now;
                return false;
            }
```

The `_captureKeysDown.Remove(vk)` at `:68` and the `_captureKeysDown.Count`
check at `:58` work unchanged for a dictionary.

- [ ] **Step 5: Prune `_captureKeysDown` too**

In `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs`, replace the `PruneStaleKeys`
helper added in Task 1:

```csharp
    private void PruneStaleKeys(DateTimeOffset now, int exceptVk)
    {
        PruneStale(_swallowedKeys, now, exceptVk);
    }
```

with:

```csharp
    private void PruneStaleKeys(DateTimeOffset now, int exceptVk)
    {
        PruneStale(_swallowedKeys, now, exceptVk);
        PruneStale(_captureKeysDown, now, exceptVk);
    }
```

- [ ] **Step 6: Build & run to verify GREEN**

Run:
```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Platform.Tests/bin/Debug/net9.0/Winpepper.Platform.Tests.dll \
  --filter-class "Winpepper.Platform.Tests.Hotkeys.CaptureDrainSelfHealTests"
```
Expected: **1 passed, 0 failed.** On the `VK_RCONTROL` down event, `PruneStaleKeys`
sees `A` physically up and drops it, `_captureKeysDown.Count` becomes 0, the drain
branch is skipped, and `RightCtrl+RightShift` fires `HoldDown`.

- [ ] **Step 7: Run the full non-Windows Platform suite (no regressions)**

Run:
```bash
dotnet exec tests/Winpepper.Platform.Tests/bin/Debug/net9.0/Winpepper.Platform.Tests.dll \
  --filter-not-trait "Platform=Windows"
```
Expected: **0 failed.** In particular
`ResumeAfterCapture_WaitsForCapturedKeysAndRepeatsToBeReleased` still passes:
with the default probe returning `true` on Linux and a fresh (non-advancing)
clock, captured keys stay live until their real key-up, so drain behavior is
unchanged.

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.Platform/Hotkeys/HotkeyHook.cs \
        tests/Winpepper.Platform.Tests/Hotkeys/CaptureDrainSelfHealTests.cs
git commit -m "$(cat <<'EOF'
feat: self-heal capture-drain wedge in hotkey hook

A lost key-up for a key captured during chord recording kept _captureKeysDown
non-empty, wedging the hook in drain mode and silently killing all hotkeys
until restart. Track captured keys with timestamps and prune stale entries so
drain mode recovers on the next key event.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 3: Guarantee suspend release on recorder teardown (defect 3)

**Files:**
- Create: `src/Winpepper.Platform/Hotkeys/RecorderSuspendCoordinator.cs`
- Create: `tests/Winpepper.Platform.Tests/Hotkeys/RecorderSuspendCoordinatorTests.cs`
- Modify: `src/Winpepper.App/Views/Controls/HotkeyRecorderBox.xaml.cs`
  (Windows-only; not built/tested on Linux)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces (relied on by the recorder box):
  - `RecorderSuspendCoordinator(Action<bool> suspendSink)`
  - `void RecorderSuspendCoordinator.SetRecording(bool recording)` — idempotent
    per state; forwards a real transition to the sink.
  - `void RecorderSuspendCoordinator.Teardown()` — releases suspend (sink(false))
    iff currently suspended; idempotent.
  - `bool RecorderSuspendCoordinator.IsSuspended { get; }`

- [ ] **Step 1: Write the failing coordinator tests**

Create `tests/Winpepper.Platform.Tests/Hotkeys/RecorderSuspendCoordinatorTests.cs`
with exactly this content:

```csharp
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

/// <summary>
/// End-user story: if the hotkey recorder control is torn down (window closed,
/// page unloaded) while it still has the global hook suspended for capture, the
/// hook must be un-suspended - otherwise every global hotkey is silently dead
/// until restart. The WinUI control is untestable on Linux, so the "always
/// release on teardown" guarantee lives in this pure-managed coordinator.
/// </summary>
public class RecorderSuspendCoordinatorTests
{
    [Fact]
    public void Teardown_WhileRecording_ReleasesSuspend()
    {
        var states = new List<bool>();
        var coord = new RecorderSuspendCoordinator(states.Add);

        coord.SetRecording(true);   // recorder armed -> suspend on
        coord.Teardown();           // control unloaded without Cancel/Commit

        states.ShouldBe(new[] { true, false });
        coord.IsSuspended.ShouldBeFalse();
    }

    [Fact]
    public void Teardown_WhenNotRecording_IsNoOp()
    {
        var states = new List<bool>();
        var coord = new RecorderSuspendCoordinator(states.Add);

        coord.Teardown();

        states.ShouldBeEmpty();
        coord.IsSuspended.ShouldBeFalse();
    }

    [Fact]
    public void Teardown_AfterNormalStop_DoesNotDoubleRelease()
    {
        var states = new List<bool>();
        var coord = new RecorderSuspendCoordinator(states.Add);

        coord.SetRecording(true);
        coord.SetRecording(false);  // committed / cancelled normally
        coord.Teardown();           // later unload

        states.ShouldBe(new[] { true, false });
    }

    [Fact]
    public void SetRecording_IsIdempotentPerState()
    {
        var states = new List<bool>();
        var coord = new RecorderSuspendCoordinator(states.Add);

        coord.SetRecording(true);
        coord.SetRecording(true);
        coord.SetRecording(false);
        coord.SetRecording(false);

        states.ShouldBe(new[] { true, false });
    }
}
```

- [ ] **Step 2: Build & run to verify they FAIL (compile RED)**

Run:
```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Debug -f net9.0
```
Expected: **build FAILS** with `CS0246` — `RecorderSuspendCoordinator` does not
exist yet. This is the RED state.

- [ ] **Step 3: Create the coordinator**

Create `src/Winpepper.Platform/Hotkeys/RecorderSuspendCoordinator.cs` with
exactly this content:

```csharp
namespace Winpepper.Platform.Hotkeys;

/// <summary>
/// Guarantees the global hotkey hook is never left suspended when a hotkey
/// recorder control is torn down. The recorder UI (HotkeyRecorderBox) is
/// WinUI-only and hard to test, so the "suspend must be released on teardown"
/// rule lives here as pure managed logic. <see cref="SetRecording"/> forwards
/// only real state transitions to the sink; <see cref="Teardown"/> always
/// drives the sink back to "not suspended" if it is still suspended.
/// </summary>
public sealed class RecorderSuspendCoordinator
{
    private readonly Action<bool> _suspendSink;
    private bool _suspended;

    public RecorderSuspendCoordinator(Action<bool> suspendSink)
        => _suspendSink = suspendSink ?? throw new ArgumentNullException(nameof(suspendSink));

    /// <summary>True while the hook is suspended on this recorder's behalf.</summary>
    public bool IsSuspended => _suspended;

    /// <summary>
    /// The recorder started (true) or stopped (false) capturing. Idempotent:
    /// only a real transition is forwarded to the suspend sink.
    /// </summary>
    public void SetRecording(bool recording)
    {
        if (recording == _suspended) return;
        _suspended = recording;
        _suspendSink(recording);
    }

    /// <summary>
    /// Called on recorder Unloaded / dispose / window close. Releases suspend if
    /// it is still held, so a torn-down recorder can never leave hotkeys dead.
    /// </summary>
    public void Teardown()
    {
        if (!_suspended) return;
        _suspended = false;
        _suspendSink(false);
    }
}
```

- [ ] **Step 4: Build & run the coordinator tests to verify GREEN**

Run:
```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Platform.Tests/bin/Debug/net9.0/Winpepper.Platform.Tests.dll \
  --filter-class "Winpepper.Platform.Tests.Hotkeys.RecorderSuspendCoordinatorTests"
```
Expected: **4 passed, 0 failed.**

- [ ] **Step 5: Wire the coordinator into `HotkeyRecorderBox` (Windows-only)**

In `src/Winpepper.App/Views/Controls/HotkeyRecorderBox.xaml.cs`, replace the
field declarations + constructor (currently `:16-38`):

```csharp
    private readonly ChordRecorder _recorder = new();
    private string _chordBeforeRecording = "";
    private ILogger? _logCache;
    private ILogger? Log => _logCache ??= App.Shell?.LogFactory.CreateLogger("HotkeyRecorderBox");

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(HotkeyRecorderBox), new PropertyMetadata("Hotkey",
            (d, e) => ((HotkeyRecorderBox)d).LabelBlock.Text = (string)e.NewValue));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public HotkeyRecorderBox()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        LostFocus += OnLostFocus;
        IsTabStop = true;
    }
```

with:

```csharp
    private readonly ChordRecorder _recorder = new();
    private string _chordBeforeRecording = "";
    private ILogger? _logCache;
    private ILogger? Log => _logCache ??= App.Shell?.LogFactory.CreateLogger("HotkeyRecorderBox");

    // Guarantees the global hook is un-suspended if this control is torn down
    // mid-recording (window close / page unload) without Cancel/Commit/LostFocus.
    private readonly RecorderSuspendCoordinator _suspend;

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(HotkeyRecorderBox), new PropertyMetadata("Hotkey",
            (d, e) => ((HotkeyRecorderBox)d).LabelBlock.Text = (string)e.NewValue));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public HotkeyRecorderBox()
    {
        InitializeComponent();
        _suspend = new RecorderSuspendCoordinator(recording => RecordingStateChanged?.Invoke(recording));
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        LostFocus += OnLostFocus;
        Unloaded += OnUnloaded;
        IsTabStop = true;
    }

    // Torn down (page navigated away, window closed) - release suspend and stop
    // any in-flight recording so global hotkeys can never be left dead.
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _recorder.Cancel();
        _suspend.Teardown();
    }
```

Then route every `RecordingStateChanged?.Invoke(...)` through the coordinator so
its `_suspended` mirror stays accurate. Replace the body of `OnRecordClick`
(currently `:47-57`):

```csharp
    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        _chordBeforeRecording = ChordText.Text;
        _recorder.Begin();
        RecordingStateChanged?.Invoke(true);
        ChordText.Text = "(press a chord - Esc cancels)";
        RecordButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        Log?.LogInformation("Hotkey recording started ({Label})", Label);
        Focus(FocusState.Programmatic);
    }
```

with:

```csharp
    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        _chordBeforeRecording = ChordText.Text;
        _recorder.Begin();
        _suspend.SetRecording(true);
        ChordText.Text = "(press a chord - Esc cancels)";
        RecordButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        Log?.LogInformation("Hotkey recording started ({Label})", Label);
        Focus(FocusState.Programmatic);
    }
```

Replace the body of `CancelRecording` (currently `:71-78`):

```csharp
    private void CancelRecording(string reason)
    {
        if (!_recorder.Cancel()) return;
        RecordingStateChanged?.Invoke(false);
        ChordText.Text = _chordBeforeRecording;
        ResetButtons();
        Log?.LogInformation("Hotkey recording cancelled ({Label}): {Reason}", Label, reason);
    }
```

with:

```csharp
    private void CancelRecording(string reason)
    {
        if (!_recorder.Cancel()) return;
        _suspend.SetRecording(false);
        ChordText.Text = _chordBeforeRecording;
        ResetButtons();
        Log?.LogInformation("Hotkey recording cancelled ({Label}): {Reason}", Label, reason);
    }
```

Replace the `ChordKeyResult.Cancelled` arm of `HandleRecorderResult` (currently
`:138-144`):

```csharp
            case ChordKeyResult.Cancelled:
                RecordingStateChanged?.Invoke(false);
                ChordText.Text = _chordBeforeRecording;
                ResetButtons();
                Log?.LogInformation("Hotkey recording cancelled ({Label}): Esc", Label);
                e.Handled = true;
                break;
```

with:

```csharp
            case ChordKeyResult.Cancelled:
                _suspend.SetRecording(false);
                ChordText.Text = _chordBeforeRecording;
                ResetButtons();
                Log?.LogInformation("Hotkey recording cancelled ({Label}): Esc", Label);
                e.Handled = true;
                break;
```

Replace the body of `CommitRecordedChord` (currently `:156-164`):

```csharp
    private void CommitRecordedChord()
    {
        var chord = _recorder.CommittedChord!;
        SetChord(chord, null);
        ResetButtons();
        ChordRecorded?.Invoke(chord);
        RecordingStateChanged?.Invoke(false);
        Log?.LogInformation("Hotkey recording committed ({Label}): {Chord}", Label, chord);
    }
```

with:

```csharp
    private void CommitRecordedChord()
    {
        var chord = _recorder.CommittedChord!;
        SetChord(chord, null);
        ResetButtons();
        ChordRecorded?.Invoke(chord);
        _suspend.SetRecording(false);
        Log?.LogInformation("Hotkey recording committed ({Label}): {Chord}", Label, chord);
    }
```

- [ ] **Step 6: Verify the Platform suite is still green (Linux gate)**

The `HotkeyRecorderBox.xaml.cs` change is Windows-only (`#if WINDOWS`) and is not
built on Linux; its guarantee is proven by `RecorderSuspendCoordinatorTests`.
Re-run the full non-Windows suite to confirm nothing else regressed:

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Platform.Tests/bin/Debug/net9.0/Winpepper.Platform.Tests.dll \
  --filter-not-trait "Platform=Windows"
```
Expected: **0 failed.**

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Platform/Hotkeys/RecorderSuspendCoordinator.cs \
        src/Winpepper.App/Views/Controls/HotkeyRecorderBox.xaml.cs \
        tests/Winpepper.Platform.Tests/Hotkeys/RecorderSuspendCoordinatorTests.cs
git commit -m "$(cat <<'EOF'
feat: guarantee hotkey suspend is released on recorder teardown

If the hotkey recorder control was torn down (window close / page unload) while
still suspending the global hook for capture, suspend leaked and every hotkey
went silently dead until restart. Add a pure-managed RecorderSuspendCoordinator
that always releases suspend on Unloaded, and route the WinUI recorder box
through it.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 4: Reject unsafe modifier-less trigger bindings (defect 4)

**Files:**
- Modify: `src/Winpepper.Platform/Hotkeys/HotkeyChord.cs` (add
  `ValidateTriggerBinding` and `ParseTriggerOrDefault` near the end of the
  record, after `Parse` at `:87`)
- Modify: `src/Winpepper.Platform/Hotkeys/PlatformHotkeyValidator.cs` (enforce
  in `Validate`)
- Modify: `src/Winpepper.App/Hosting/AppShell.cs` (load hold/toggle via
  `ParseTriggerOrDefault`; Windows-only, not built/tested on Linux)
- Create: `tests/Winpepper.Platform.Tests/Hotkeys/HotkeyChordValidationTests.cs`

**Interfaces:**
- Consumes: `HotkeyChord.Parse(string)`, `HotkeyChord.Modifiers`,
  `HotkeyChord.VirtualKey`, `Modifier.None`, `HotkeyConflicts.Describe`.
- Produces:
  - `static string? HotkeyChord.ValidateTriggerBinding(HotkeyChord chord, HotkeyChord cancel)`
    — returns `null` when the binding is safe, else a human-readable reason.
  - `static HotkeyChord HotkeyChord.ParseTriggerOrDefault(string configured,
    string defaultChord, HotkeyChord cancel, Action<string>? onRejected = null)`
    — parses and policy-checks `configured`; on parse failure or unsafe binding
    returns `Parse(defaultChord)` and invokes `onRejected` with a message.

- [ ] **Step 1: Write the failing validation tests**

Create `tests/Winpepper.Platform.Tests/Hotkeys/HotkeyChordValidationTests.cs`
with exactly this content:

```csharp
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

/// <summary>
/// End-user story: a hand-edited settings file (or a future UI path) must not be
/// able to bind a bare common key (Esc, Tab, Enter, Space, a letter, a digit, an
/// arrow) as a hold/toggle trigger, because the hook swallows the trigger and
/// that key would then be dead system-wide - the exact "Esc broken everywhere"
/// failure. Bare F-keys stay allowed (conventional global hotkeys); modifier+key
/// and modifier-only chords stay allowed; the Cancel key can never be a trigger.
/// </summary>
public class HotkeyChordValidationTests
{
    private static readonly HotkeyChord Cancel = HotkeyChord.Parse("Esc");

    [Theory]
    [InlineData("Esc")]
    [InlineData("Tab")]
    [InlineData("Enter")]
    [InlineData("Back")]
    [InlineData("Space")]
    [InlineData("Delete")]
    [InlineData("Left")]
    [InlineData("A")]
    [InlineData("5")]
    public void ValidateTriggerBinding_BareCommonKey_IsRejected(string chord)
        => HotkeyChord.ValidateTriggerBinding(HotkeyChord.Parse(chord), Cancel).ShouldNotBeNull();

    [Theory]
    [InlineData("Ctrl+Shift+Space")]
    [InlineData("LeftAlt+F12")]
    [InlineData("LeftCtrl+A")]
    [InlineData("RightCtrl+RightShift")] // modifier-only: trigger is a modifier
    [InlineData("F1")]                   // bare F-keys are allowed
    [InlineData("F12")]
    public void ValidateTriggerBinding_SafeBinding_IsAccepted(string chord)
        => HotkeyChord.ValidateTriggerBinding(HotkeyChord.Parse(chord), Cancel).ShouldBeNull();

    [Fact]
    public void ValidateTriggerBinding_TriggerEqualsCancelKey_IsRejected()
        => HotkeyChord.ValidateTriggerBinding(HotkeyChord.Parse("Ctrl+Esc"), Cancel).ShouldNotBeNull();

    [Fact]
    public void ParseTriggerOrDefault_UnsafeBareEsc_FallsBackToDefault_AndWarns()
    {
        string? warned = null;
        var chord = HotkeyChord.ParseTriggerOrDefault(
            "Esc", "Ctrl+Shift+Space", Cancel, m => warned = m);

        chord.ShouldBe(HotkeyChord.Parse("Ctrl+Shift+Space"));
        warned.ShouldNotBeNull();
    }

    [Fact]
    public void ParseTriggerOrDefault_SafeBinding_IsKept()
    {
        var chord = HotkeyChord.ParseTriggerOrDefault(
            "LeftAlt+F12", "Ctrl+Shift+Space", Cancel);

        chord.ShouldBe(HotkeyChord.Parse("LeftAlt+F12"));
    }

    [Fact]
    public void ParseTriggerOrDefault_UnparseableValue_FallsBackToDefault()
    {
        var chord = HotkeyChord.ParseTriggerOrDefault(
            "Ctrl+NotAKey", "RightCtrl+RightShift", Cancel);

        chord.ShouldBe(HotkeyChord.Parse("RightCtrl+RightShift"));
    }
}
```

- [ ] **Step 2: Build & run to verify they FAIL (compile RED)**

Run:
```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Debug -f net9.0
```
Expected: **build FAILS** with `CS0117` — `HotkeyChord` has no
`ValidateTriggerBinding` / `ParseTriggerOrDefault`. This is the RED state.

- [ ] **Step 3: Add the validation policy to `HotkeyChord`**

In `src/Winpepper.Platform/Hotkeys/HotkeyChord.cs`, add these members immediately
after the `Parse` method (after `:87`, before the `Matches` XML doc at `:89`):

```csharp
    // VK_F1 (0x70) .. VK_F24 (0x87). Only function keys are conventional as
    // bare (modifier-less) global hotkeys; every other common key would be
    // swallowed system-wide if bound as a hold/toggle trigger.
    private static bool IsBareTriggerAllowed(int vk) => vk is >= 0x70 and <= 0x87;

    /// <summary>
    /// Policy gate for hold/toggle trigger bindings. Returns null when the
    /// binding is safe, otherwise a human-readable reason it was rejected.
    /// Enforced both in the settings UI validator and when loading a hand-edited
    /// settings file. Rules:
    ///  * A modifier-only chord (VirtualKey == 0) is safe - its trigger is a
    ///    modifier, which the hook never swallows.
    ///  * A chord with modifiers plus a non-modifier key is safe.
    ///  * A bare (modifier-less) non-modifier key is rejected UNLESS it is an
    ///    F-key, because the hook swallows the trigger and a bare common key
    ///    (Esc/Tab/Enter/Space/letter/digit/arrow/...) would then be dead
    ///    system-wide.
    ///  * The trigger key may never equal the Cancel chord's key, so the
    ///    pass-through Cancel/Esc key can never be turned into a swallowed trigger.
    /// </summary>
    public static string? ValidateTriggerBinding(HotkeyChord chord, HotkeyChord cancel)
    {
        ArgumentNullException.ThrowIfNull(chord);
        ArgumentNullException.ThrowIfNull(cancel);

        if (chord.VirtualKey != 0 && cancel.VirtualKey != 0 && chord.VirtualKey == cancel.VirtualKey)
            return $"'{chord}' uses the Cancel key, which must stay available system-wide.";

        if (chord.VirtualKey == 0) return null;                  // modifier-only: safe
        if (chord.Modifiers != Modifier.None) return null;       // modifier + key: safe
        if (IsBareTriggerAllowed(chord.VirtualKey)) return null; // bare F-key: safe

        return $"'{chord}' has no modifier and would be swallowed system-wide. " +
               "Add a modifier (e.g. Ctrl+Shift+...) or use an F-key.";
    }

    /// <summary>
    /// Parses a configured hold/toggle chord and enforces
    /// <see cref="ValidateTriggerBinding"/>. If the value cannot be parsed or is
    /// an unsafe trigger, returns <paramref name="defaultChord"/> parsed and
    /// invokes <paramref name="onRejected"/> with a warning message, so a
    /// hand-edited settings file can never bind a swallowed common key.
    /// </summary>
    public static HotkeyChord ParseTriggerOrDefault(
        string configured, string defaultChord, HotkeyChord cancel, Action<string>? onRejected = null)
    {
        HotkeyChord parsed;
        try
        {
            parsed = Parse(configured);
        }
        catch (FormatException ex)
        {
            onRejected?.Invoke($"Hotkey '{configured}' is invalid ({ex.Message}); using default '{defaultChord}'.");
            return Parse(defaultChord);
        }

        var reason = ValidateTriggerBinding(parsed, cancel);
        if (reason is not null)
        {
            onRejected?.Invoke($"Hotkey '{configured}' rejected: {reason} Using default '{defaultChord}'.");
            return Parse(defaultChord);
        }

        return parsed;
    }
```

- [ ] **Step 4: Build & run the validation tests to verify GREEN**

Run:
```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Platform.Tests/bin/Debug/net9.0/Winpepper.Platform.Tests.dll \
  --filter-class "Winpepper.Platform.Tests.Hotkeys.HotkeyChordValidationTests"
```
Expected: **all passed, 0 failed** (9 bare-key rejects + 6 safe accepts + 4
`Fact`s).

- [ ] **Step 5: Enforce the policy in the settings UI validator**

In `src/Winpepper.Platform/Hotkeys/PlatformHotkeyValidator.cs`, replace the whole
class body (currently `:5-19`):

```csharp
public sealed class PlatformHotkeyValidator : IHotkeyValidator
{
    public string? Validate(string chord)
    {
        HotkeyChord parsed;
        try { parsed = HotkeyChord.Parse(chord); }
        catch (FormatException ex) { return ex.Message; }
        return HotkeyConflicts.Describe(parsed);
    }

    public bool Clash(string a, string b)
    {
        try { return HotkeyConflicts.HoldAndToggleClash(HotkeyChord.Parse(a), HotkeyChord.Parse(b)); }
        catch { return false; }
    }
}
```

with:

```csharp
public sealed class PlatformHotkeyValidator : IHotkeyValidator
{
    // Cancel is always Esc (see AppShell / ChordRecorder); a trigger may never
    // reuse it.
    private static readonly HotkeyChord CancelChord = HotkeyChord.Parse("Esc");

    public string? Validate(string chord)
    {
        HotkeyChord parsed;
        try { parsed = HotkeyChord.Parse(chord); }
        catch (FormatException ex) { return ex.Message; }

        var conflict = HotkeyConflicts.Describe(parsed);
        if (conflict is not null) return conflict;

        // Reject modifier-less common-key triggers (they'd be swallowed globally)
        // and any trigger that reuses the Cancel key.
        return HotkeyChord.ValidateTriggerBinding(parsed, CancelChord);
    }

    public bool Clash(string a, string b)
    {
        try { return HotkeyConflicts.HoldAndToggleClash(HotkeyChord.Parse(a), HotkeyChord.Parse(b)); }
        catch { return false; }
    }
}
```

- [ ] **Step 6: Enforce fall-back-to-default in the settings load path (Windows-only)**

In `src/Winpepper.App/Hosting/AppShell.cs`, replace the chord parsing block
(currently `:186-188`):

```csharp
        var hold   = HotkeyChord.Parse(settings.HoldHotkey);
        var toggle = HotkeyChord.Parse(settings.ToggleHotkey);
        var cancel = HotkeyChord.Parse("Esc");
```

with:

```csharp
        var cancel = HotkeyChord.Parse("Esc");
        var hotkeyLog = factory.CreateLogger("Winpepper.App.Hotkeys");
        // A hand-edited settings file must never bind a bare common key (it would
        // be swallowed system-wide). Unsafe/invalid values fall back to the
        // built-in defaults with a logged warning.
        var hold   = HotkeyChord.ParseTriggerOrDefault(
            settings.HoldHotkey, "RightCtrl+RightShift", cancel,
            m => hotkeyLog.LogWarning("{HotkeyWarning}", m));
        var toggle = HotkeyChord.ParseTriggerOrDefault(
            settings.ToggleHotkey, "Ctrl+Shift+Space", cancel,
            m => hotkeyLog.LogWarning("{HotkeyWarning}", m));
```

(The default strings match `AppSettings.HoldHotkey` = `"RightCtrl+RightShift"`
and `AppSettings.ToggleHotkey` = `"Ctrl+Shift+Space"`. `AppShell.cs` is
`#if WINDOWS`-guarded and is not built on Linux; the logic it calls,
`ParseTriggerOrDefault`, is fully covered by Task 4 Step 1.)

- [ ] **Step 7: Run the full non-Windows Platform suite (no regressions)**

Run:
```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Platform.Tests/bin/Debug/net9.0/Winpepper.Platform.Tests.dll \
  --filter-not-trait "Platform=Windows"
```
Expected: **0 failed.** The only defaults shipped (`RightCtrl+RightShift`,
`Ctrl+Shift+Space`) and every chord asserted valid in existing tests are safe
under the new policy, so no existing test flips.

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.Platform/Hotkeys/HotkeyChord.cs \
        src/Winpepper.Platform/Hotkeys/PlatformHotkeyValidator.cs \
        src/Winpepper.App/Hosting/AppShell.cs \
        tests/Winpepper.Platform.Tests/Hotkeys/HotkeyChordValidationTests.cs
git commit -m "$(cat <<'EOF'
feat: reject unsafe modifier-less hotkey trigger bindings

A hand-edited settings file could bind a bare common key (Esc, Tab, Enter,
Space, a letter, ...) as a hold/toggle trigger; the hook swallows the trigger,
so that key would be dead system-wide - the exact "Esc broken everywhere"
failure. Add a trigger validation policy: bare common keys and the Cancel key
are rejected (bare F-keys and modifier+key chords stay allowed). Enforced in the
settings UI validator and on settings load (unsafe values fall back to the
default with a logged warning).

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

## Self-Review

**1. Spec coverage.**
- **Defect 1 (stuck `_swallowedKeys`)** → Task 1: `_swallowedKeys` becomes a
  timestamped dictionary; the autorepeat guard and a stale sweep drop entries
  when the physical key is up (`GetAsyncKeyState` seam) or the entry outlives
  `StaleKeyTimeout`. Proven by
  `SwallowedTrigger_LostKeyUp_PhysicalRelease_FreshPressPassesThrough` (critical
  test a), `..._HealedByExpiry_...`, and
  `HeldTrigger_Autorepeat_StillSwallowed_WhilePhysicallyDown` (guards against
  false-healing a genuine hold). ✓
- **Defect 2 (drain wedge)** → Task 2: `_captureKeysDown` becomes a timestamped
  dictionary pruned by the same `PruneStaleKeys`. Proven by
  `CaptureDrain_LostKeyUp_RecoversOnNextEvent_HotkeysResume` (critical test b). ✓
- **Defect 3 (suspend leak)** → Task 3: `RecorderSuspendCoordinator.Teardown()`
  always releases suspend, wired to `HotkeyRecorderBox.Unloaded`. Proven by
  `RecorderSuspendCoordinatorTests` (critical test c, at the practical
  pure-managed seam). ✓
- **Defect 4 (unsafe bindings)** → Task 4: `ValidateTriggerBinding` /
  `ParseTriggerOrDefault` reject bare common-key triggers and the Cancel VK,
  allow modifier+key, modifier-only, and bare F-keys; enforced in
  `PlatformHotkeyValidator` and the `AppShell` load path. Proven by
  `HotkeyChordValidationTests` (critical test d). ✓
- **Invariant preservation** → existing `HotkeyHookLogicTests`,
  `ModifierPassthroughTests` (modifiers never swallowed),
  `CancelChord_PlainEsc_EmitsCancelWithoutSwallowing` (Esc pass-through), and
  key-up symmetry tests are re-run green at each task's regression step (1.9,
  2.7, 3.6, 4.7). Default probe returns `true` on Linux and the default clock
  does not advance, so pre-existing behavior is byte-for-byte preserved. ✓

**1b. No silent deferrals.** No stubs, mocks, fake providers, or synthetic
seams. Every requirement is proven against real production logic:
- `TryProcessKey` is the genuine per-key decision consumed by `HookCallback`
  (`swallow ? (IntPtr)1 : CallNextHookEx(...)`); the injected clock/probe are
  test *inputs* to real code, not substitutes for it. On Windows the default
  probe is the real `GetAsyncKeyState`.
- `RecorderSuspendCoordinator` is the real teardown-guarantee object the
  production `HotkeyRecorderBox` uses (wired in Task 3 Step 5), not a test
  double.
- `ParseTriggerOrDefault` is the real load-path function wired into `AppShell`
  (Task 4 Step 6) and `PlatformHotkeyValidator` (Task 4 Step 5).
- The two Windows-only edits (`HotkeyRecorderBox.xaml.cs`, `AppShell.cs`) are
  thin wiring around fully-tested pure-managed logic; they are the production
  integration points, explicitly called out, not deferred behavior. This is the
  spec-sanctioned "testable at whatever seam is practical" (defect 3) and is not
  a coverage gap. No requirement is moved to "future work". No UNRESOLVED
  COVERAGE GAP.

**2. Placeholder scan.** No "TBD"/"TODO"/"handle edge cases"/"similar to Task N".
Every code step shows complete, copy-pastable code and every run step gives an
exact command and expected result. RED steps that are compile failures are
labeled as such (C# TDD reality, documented in Global Constraints).

**3. Type consistency.** Verified across tasks:
- `HotkeyHook` constructor signature `(HotkeyChord, HotkeyChord, HotkeyChord,
  ILogger<HotkeyHook>, Func<bool>?, Func<DateTimeOffset>?, Func<int,bool>?)` is
  defined in Task 1 Step 5 and used with named args `timeProvider:` /
  `keyPhysicallyDown:` in Tasks 1 and 2 test helpers — names match exactly.
- `_swallowedKeys` and `_captureKeysDown` are both `Dictionary<int,DateTimeOffset>`;
  `.Remove(vk)` (bool) and `[vk] = now` are used consistently.
- `PruneStaleKeys(DateTimeOffset now, int exceptVk)` defined in Task 1 Step 6,
  extended (not renamed) in Task 2 Step 5; `IsKeyEntryLive(int, DateTimeOffset,
  DateTimeOffset)` and `StaleKeyTimeout` used consistently.
- `RecorderSuspendCoordinator.{SetRecording(bool), Teardown(), IsSuspended}` and
  ctor `(Action<bool>)` match between Task 3 Steps 1, 3, and 5.
- `HotkeyChord.ValidateTriggerBinding(HotkeyChord, HotkeyChord)` and
  `ParseTriggerOrDefault(string, string, HotkeyChord, Action<string>?)` match
  between Task 4 Steps 1, 3, 5, and 6.
- VK constants (`VK_LCONTROL` … `VK_RWIN`, `Space=0x20`, `A=0x41`) and
  `HotkeyEventKind.{Toggle,HoldDown,HoldUp,Cancel}` match the existing source.
