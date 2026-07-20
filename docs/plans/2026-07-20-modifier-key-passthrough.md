# Modifier Key Pass-Through Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Ensure any key the user configures as a *modifier* in a hotkey
(e.g. Shift) still flows through to Windows and keeps working system-wide —
the app must never swallow/disable a modifier key just because it
participates in hotkey handling.

**Architecture:** The global low-level keyboard hook
(`Winpepper.Platform.Hotkeys.HotkeyHook`) decides, per key event, whether to
*swallow* the event (hide it from Windows/the foreground app) or let it pass
through. Today it swallows the *completing key* of a chord — and for
modifier-only chords that completing key is a modifier (Shift, Ctrl, …),
which disables that modifier everywhere. The fix makes swallowing apply only
to **non-modifier trigger keys**; modifier keys always pass through while
still firing the hotkey event.

**Tech Stack:** C# / .NET 9 (`net9.0` + `net9.0-windows10.0.19041.0`
multi-target), xUnit v3, Shouldly assertions. All logic here is exercised
through `HotkeyHook.TryProcessKey`, a pure managed method that runs
cross-platform (no P/Invoke), so tests run on Linux.

## Global Constraints

- **Environment prerequisite (verify before Task 1):** every RED/GREEN step in
  this plan runs `dotnet test`, so a .NET SDK satisfying `global.json`
  (`sdk 9.0.100`, `rollForward: latestFeature`) must be installed and on `PATH`.
  Confirm with `dotnet --version` before running any step. If `dotnet` is absent
  (e.g. a bare Linux checkout without the SDK — a known state of this worktree),
  provision the SDK first, or run the `dotnet test` gates on the Windows VM / CI
  where the SDK is present (the repo's established pattern — see `README.md`).
  All logic here is pure managed code (`net9.0`, no P/Invoke), so once the SDK is
  available the tests run on Linux.
- Target framework for running/inspecting logic tests on Linux: `net9.0`
  (the `net9.0-windows10.0.19041.0` target and `[Trait("Platform","Windows")]`
  tests are Windows-only and are excluded via `--filter "Platform!=Windows"`).
- Test framework: **xUnit v3** (`xunit.v3` package) with **Shouldly**
  assertions (`ShouldBe`, `ShouldBeTrue`, `ShouldBeFalse`, `ShouldBeNull`).
- Invariant introduced by this plan (must hold for every task): **a modifier
  key event is NEVER swallowed.** `HotkeyHook.TryProcessKey` must return
  `false` (pass through to Windows) for every Ctrl/Shift/Alt/Win key (left or
  right), whether or not that key completes/participates in a chord.
- Non-goal (explicitly preserved): a **non-modifier trigger key** (e.g. the
  `Space` in `Ctrl+Shift+Space`) is still swallowed, so pressing a hotkey does
  not leak that key into the focused app. Only modifier keys change behavior.
- Commit style: focused, atomic commits; use `feat:`/`test:`/`docs:` prefixes.
  Include the standard Amplifier co-author trailer used by this repo.
- `README.md` is the only end-user markdown doc; this plan under `docs/plans/`
  is a working/agent doc.

---

## Root Cause (read before starting)

In `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs`, `TryProcessKey` records the
completing key of an activated chord in `_swallowedKeys` and returns `true`
(swallow) at two sites:

- Toggle activation:
  ```csharp
  evt = new HotkeyEvent(HotkeyEventKind.Toggle, DateTimeOffset.UtcNow);
  _swallowedKeys.Add(vk);
  return true;
  ```
- Hold activation:
  ```csharp
  _holding = true;
  evt = new HotkeyEvent(HotkeyEventKind.HoldDown, DateTimeOffset.UtcNow);
  _swallowedKeys.Add(vk);
  return true;
  ```

For a **modifier-only** chord such as `RightCtrl+RightShift` or
`LeftCtrl+LeftShift`, the completing key is a modifier (e.g. `VK_RSHIFT`). It
is added to `_swallowedKeys`, so both its key-down and (symmetrically) its
key-up are hidden from Windows. Result: while that hotkey exists, the Shift
(or Ctrl/Alt/Win) key is disabled system-wide.

For a **mixed** chord such as `Ctrl+Shift+Space`, the modifiers already pass
through (only `Space` matches `chord.VirtualKey` and gets swallowed), so those
are already correct — no change needed there.

**Fix (minimal & complete):** never add a modifier key to `_swallowedKeys`
and never return `true` for one. Because `_swallowedKeys` will then never
contain a modifier VK, every other swallow site that consults it (autorepeat
guard, key-up removal, suspended-drain removal) automatically passes modifiers
through — so the *only* code change is guarding the two activation sites with
an `IsModifierKey(vk)` check. Duplicate-event suppression for modifier-only
chords is already handled by `ActivatesOnKeyDown` (it only activates on the
incomplete→complete transition, so an autorepeat of a held modifier does not
re-fire), so removing the swallow does not reintroduce duplicate events.

This changes the *contract* that six existing tests in
`HotkeyHookLogicTests.cs` asserted (they expected the completing modifier to
be swallowed). Those tests are updated in Task 1 to assert pass-through.

---

## File Structure

- `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs` — **modified.** Add a private
  `IsModifierKey(int)` helper; guard the Toggle and Hold swallow sites so
  modifier keys pass through; update the `TryProcessKey` XML doc to state the
  invariant.
- `tests/Winpepper.Platform.Tests/Hotkeys/ModifierPassthroughTests.cs` —
  **created.** End-user-story tests proving modifier keys pass through
  (hold-modifier-only, toggle-modifier-only, mixed chord) and, in Task 2, an
  exhaustive parameterized test over all eight modifier virtual keys.
- `tests/Winpepper.Platform.Tests/Hotkeys/HotkeyHookLogicTests.cs` —
  **modified.** Reconcile the six tests that encoded the old
  swallow-the-modifier contract to the new pass-through contract.

No production consumer changes are required: the only production caller of
`TryProcessKey` is `HotkeyHook.HookCallback`, which already maps the returned
`swallow` bool to "call the next hook / pass to Windows" — so returning
`false` for modifiers is exactly the desired OS pass-through.

---

## Scope Check

This is a single, cohesive subsystem (the keyboard hook's swallow decision).
It does not warrant splitting into multiple plans. Two tasks: Task 1 makes the
behavior change with focused coverage and reconciles existing tests; Task 2
adds exhaustive regression coverage across every modifier key so the invariant
cannot silently regress for Alt/Win (which the logic tests never exercised).

---

### Task 1: Modifier keys pass through to Windows (core contract change)

**Files:**
- Create: `tests/Winpepper.Platform.Tests/Hotkeys/ModifierPassthroughTests.cs`
- Modify: `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs`
  (the `TryProcessKey` Toggle/Hold activation sites near lines 84–102, the XML
  doc above `TryProcessKey`, and add a helper next to `ModifierForVirtualKey`)
- Modify: `tests/Winpepper.Platform.Tests/Hotkeys/HotkeyHookLogicTests.cs`
  (six existing tests)

**Interfaces:**
- Consumes (already exists, do not redefine):
  - `bool HotkeyHook.TryProcessKey(int vk, bool down, out HotkeyEvent? evt)` —
    returns `true` to swallow (hide from Windows), `false` to pass through.
  - `HotkeyHook(HotkeyChord hold, HotkeyChord toggle, HotkeyChord cancel,
    ILogger<HotkeyHook> log, Func<bool>? cancelEnabled = null)`.
  - `HotkeyChord.Parse(string)`, `HotkeyEventKind.{HoldDown,HoldUp,Toggle,Cancel}`.
  - Modifier VK constants from `Winpepper.Platform.Hotkeys.KeyboardHookNative`
    (import via `using static`): `VK_LCONTROL=0xA3`? — use the named
    constants: `VK_LCONTROL, VK_RCONTROL, VK_LSHIFT, VK_RSHIFT, VK_LMENU,
    VK_RMENU, VK_LWIN, VK_RWIN`.
- Produces (new, relied on by Task 2):
  - `HotkeyHook` now returns `false` from `TryProcessKey` for any modifier
    virtual key, while still setting `evt` for the fired hotkey.
  - New private helper `static bool HotkeyHook.IsModifierKey(int vk)` (internal
    detail; Task 2 does not call it directly, it asserts the behavior).

- [ ] **Step 1: Write the failing end-user-story tests**

Create `tests/Winpepper.Platform.Tests/Hotkeys/ModifierPassthroughTests.cs`
with exactly this content:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;
using static Winpepper.Platform.Hotkeys.KeyboardHookNative;

namespace Winpepper.Platform.Tests.Hotkeys;

/// <summary>
/// End-user story: a key the user chose as a modifier in their hotkey must
/// keep working everywhere in Windows. The hook may observe the key and fire
/// its event, but it must never hide (swallow) a modifier key from the OS.
/// "Swallow" is the bool returned by <see cref="HotkeyHook.TryProcessKey"/>:
/// true hides the event from Windows / the foreground app; false lets it flow
/// through to the OS.
/// </summary>
public class ModifierPassthroughTests
{
    private static HotkeyHook NewHook(string hold,
                                       string toggle = "LeftAlt+F12",
                                       string cancel = "Esc")
        => new(HotkeyChord.Parse(hold), HotkeyChord.Parse(toggle),
               HotkeyChord.Parse(cancel), new NullLogger<HotkeyHook>());

    [Fact]
    public void HoldModifierOnlyChord_CompletingModifier_PassesThroughToWindows()
    {
        var hook = NewHook(hold: "RightCtrl+RightShift");

        // RightCtrl arms the chord and already passes through.
        hook.TryProcessKey(VK_RCONTROL, down: true, out _).ShouldBeFalse();

        // RightShift completes the chord: the hold fires, but Shift must still
        // reach Windows so it keeps shifting system-wide.
        hook.TryProcessKey(VK_RSHIFT, down: true, out var down).ShouldBeFalse();
        down!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        // Release is symmetric: the Shift up also passes through.
        hook.TryProcessKey(VK_RSHIFT, down: false, out var up).ShouldBeFalse();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
        hook.TryProcessKey(VK_RCONTROL, down: false, out _).ShouldBeFalse();
    }

    [Fact]
    public void ToggleModifierOnlyChord_CompletingModifier_PassesThroughToWindows()
    {
        var hook = NewHook(hold: "LeftAlt+F12", toggle: "LeftCtrl+LeftShift");

        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();

        hook.TryProcessKey(VK_LSHIFT, down: true, out var evt).ShouldBeFalse();
        evt!.Kind.ShouldBe(HotkeyEventKind.Toggle);

        hook.TryProcessKey(VK_LSHIFT, down: false, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LCONTROL, down: false, out _).ShouldBeFalse();
    }

    [Fact]
    public void MixedChord_ModifiersPassThrough_TriggerKeyIsSwallowed()
    {
        var hook = NewHook(hold: "LeftAlt+F12", toggle: "Ctrl+Shift+Space");

        // Every modifier of the chord flows through to Windows.
        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT, down: true, out _).ShouldBeFalse();

        // Only the non-modifier trigger key (Space) is hidden, so pressing the
        // hotkey does not type a space into the focused app.
        hook.TryProcessKey(0x20 /* Space */, down: true, out var evt).ShouldBeTrue();
        evt!.Kind.ShouldBe(HotkeyEventKind.Toggle);
        hook.TryProcessKey(0x20, down: false, out _).ShouldBeTrue();

        // Modifiers still release cleanly through to Windows.
        hook.TryProcessKey(VK_LSHIFT, down: false, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LCONTROL, down: false, out _).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run:
```bash
dotnet test tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj \
  -f net9.0 --filter "FullyQualifiedName~ModifierPassthroughTests"
```
Expected: **FAIL.** `HoldModifierOnlyChord_...` and
`ToggleModifierOnlyChord_...` fail with a Shouldly message like
`hook.TryProcessKey(VK_RSHIFT, down: true, out var down).ShouldBeFalse() ...
but was True` (the current code swallows the completing modifier).
`MixedChord_...` already passes. Summary shows `Failed: 2, Passed: 1`.

- [ ] **Step 3: Add the `IsModifierKey` helper**

In `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs`, add the helper immediately
above the existing `private static Modifier ModifierForVirtualKey(int vk)`
method:

```csharp
    /// <summary>
    /// True when <paramref name="vk"/> is one of the eight modifier keys
    /// (Ctrl/Shift/Alt/Win, left or right). Modifier keys are always passed
    /// through to Windows so they keep functioning system-wide, even while the
    /// app uses them in a hotkey.
    /// </summary>
    private static bool IsModifierKey(int vk) => ModifierForVirtualKey(vk) != Modifier.None;
```

- [ ] **Step 4: Guard the Toggle activation site so modifiers pass through**

In `TryProcessKey`, replace this existing block:

```csharp
            if (ActivatesOnKeyDown(bindings.Toggle, vk, modifiersBeforeEvent, _modifiers))
            {
                evt = new HotkeyEvent(HotkeyEventKind.Toggle, DateTimeOffset.UtcNow);
                _swallowedKeys.Add(vk);
                return true;
            }
```

with:

```csharp
            if (ActivatesOnKeyDown(bindings.Toggle, vk, modifiersBeforeEvent, _modifiers))
            {
                evt = new HotkeyEvent(HotkeyEventKind.Toggle, DateTimeOffset.UtcNow);
                // Modifier keys always pass through to Windows so they keep
                // working system-wide (e.g. Shift still shifts). Only a
                // non-modifier trigger key is hidden from the foreground app.
                if (IsModifierKey(vk)) return false;
                _swallowedKeys.Add(vk);
                return true;
            }
```

- [ ] **Step 5: Guard the Hold activation site so modifiers pass through**

In `TryProcessKey`, replace this existing block:

```csharp
            if (ActivatesOnKeyDown(bindings.Hold, vk, modifiersBeforeEvent, _modifiers) && !_holding)
            {
                _holding = true;
                evt = new HotkeyEvent(HotkeyEventKind.HoldDown, DateTimeOffset.UtcNow);
                _swallowedKeys.Add(vk);
                return true;
            }
```

with:

```csharp
            if (ActivatesOnKeyDown(bindings.Hold, vk, modifiersBeforeEvent, _modifiers) && !_holding)
            {
                _holding = true;
                evt = new HotkeyEvent(HotkeyEventKind.HoldDown, DateTimeOffset.UtcNow);
                // Modifier keys always pass through (see the Toggle branch
                // above); only a non-modifier trigger key is swallowed.
                if (IsModifierKey(vk)) return false;
                _swallowedKeys.Add(vk);
                return true;
            }
```

- [ ] **Step 6: Document the invariant on `TryProcessKey`**

In `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs`, replace the existing XML
doc comment directly above `public bool TryProcessKey(...)`:

```csharp
    /// <summary>
    /// Public for tests: synchronously evaluate a key event against the registered
    /// chords. The return value means "swallow this event" (hide it from the
    /// foreground app); <paramref name="evt"/> is set when a hotkey event fired.
    /// The two are independent: a key-up can emit HoldUp yet still pass through
    /// when its key-down was visible to the system.
    /// </summary>
```

with:

```csharp
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
```

- [ ] **Step 7: Run the new tests to verify they pass**

Run:
```bash
dotnet test tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj \
  -f net9.0 --filter "FullyQualifiedName~ModifierPassthroughTests"
```
Expected: **PASS.** `Passed! - Failed: 0, Passed: 3`.

- [ ] **Step 8: Reconcile the existing logic tests that encoded the old contract**

The fix changes the contract that six tests in
`tests/Winpepper.Platform.Tests/Hotkeys/HotkeyHookLogicTests.cs` asserted.
Replace each of the following six methods in full (leave every other test in
the file untouched).

Replace `HoldChord_PressAndRelease_EmitsHoldDownThenHoldUp`:

```csharp
    [Fact]
    public void HoldChord_PressAndRelease_EmitsHoldDownThenHoldUp()
    {
        var hook = NewHook();
        hook.TryProcessKey(VK_RCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_RSHIFT, down: true, out var down).ShouldBeFalse();
        down!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        hook.TryProcessKey(VK_RSHIFT, down: false, out var up).ShouldBeFalse();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
        hook.TryProcessKey(VK_RCONTROL, down: false, out _).ShouldBeFalse();
    }
```

Replace `HoldChord_ReleasingPassedThroughModifierFirst_EmitsHoldUpWithoutSwallowing`:

```csharp
    [Fact]
    public void HoldChord_ReleasingPassedThroughModifierFirst_EmitsHoldUpWithoutSwallowing()
    {
        var hook = NewHook();
        // RShift pressed first: its down passes through. RCtrl completes the
        // chord; it is a modifier, so it now also passes through.
        hook.TryProcessKey(VK_RSHIFT, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_RCONTROL, down: true, out var down).ShouldBeFalse();
        down!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        // Both key-downs reached the system, so both key-ups pass through too.
        hook.TryProcessKey(VK_RSHIFT, down: false, out var up).ShouldBeFalse();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
        hook.TryProcessKey(VK_RCONTROL, down: false, out var none).ShouldBeFalse();
        none.ShouldBeNull();
    }
```

Replace `HoldChord_AutorepeatOfCompletingModifier_SwallowedWithoutDuplicateEvent`
(rename it to reflect the new contract):

```csharp
    [Fact]
    public void HoldChord_AutorepeatOfCompletingModifier_PassesThroughWithoutDuplicateEvent()
    {
        var hook = NewHook();
        hook.TryProcessKey(VK_RCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_RSHIFT, down: true, out var evt).ShouldBeFalse();
        evt!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        // Autorepeat of the held modifier keeps passing through and must not
        // re-fire HoldDown (ActivatesOnKeyDown only fires on the
        // incomplete->complete transition).
        for (var i = 0; i < 3; i++)
        {
            hook.TryProcessKey(VK_RSHIFT, down: true, out var repeat).ShouldBeFalse();
            repeat.ShouldBeNull();
        }

        hook.TryProcessKey(VK_RSHIFT, down: false, out var up).ShouldBeFalse();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
        hook.TryProcessKey(VK_RCONTROL, down: false, out _).ShouldBeFalse();
    }
```

Replace `ModifierOnlyToggle_UnrelatedKeyWhileHeld_DoesNotToggleAgain`:

```csharp
    [Fact]
    public void ModifierOnlyToggle_UnrelatedKeyWhileHeld_DoesNotToggleAgain()
    {
        var hook = NewHook(toggle: "LeftCtrl+LeftShift");

        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT, down: true, out var toggle).ShouldBeFalse();
        toggle!.Kind.ShouldBe(HotkeyEventKind.Toggle);

        hook.TryProcessKey(0x41 /* A */, down: true, out var unrelated).ShouldBeFalse();
        unrelated.ShouldBeNull();
        hook.TryProcessKey(0x41, down: false, out _).ShouldBeFalse();

        hook.TryProcessKey(VK_LSHIFT, down: false, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LCONTROL, down: false, out _).ShouldBeFalse();
    }
```

Replace `UpdateChords_ReplacesActiveHoldChord`:

```csharp
    [Fact]
    public void UpdateChords_ReplacesActiveHoldChord()
    {
        var hook = NewHook();
        hook.UpdateChords(HotkeyChord.Parse("LeftCtrl+LeftShift"), HotkeyChord.Parse("LeftAlt+F12"));

        hook.TryProcessKey(VK_RCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_RSHIFT, down: true, out var oldChord).ShouldBeFalse();
        oldChord.ShouldBeNull();
        hook.TryProcessKey(VK_RSHIFT, down: false, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_RCONTROL, down: false, out _).ShouldBeFalse();

        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT, down: true, out var newChord).ShouldBeFalse();
        newChord!.Kind.ShouldBe(HotkeyEventKind.HoldDown);
    }
```

Replace `SuspendedHook_PassesCaptureChordThrough_AndResumesAfterward`:

```csharp
    [Fact]
    public void SuspendedHook_PassesCaptureChordThrough_AndResumesAfterward()
    {
        var hook = NewHook(hold: "LeftCtrl+LeftShift");
        hook.SetSuspended(true);

        hook.TryProcessKey(VK_LCONTROL, down: true, out var first).ShouldBeFalse();
        first.ShouldBeNull();
        hook.TryProcessKey(VK_LSHIFT, down: true, out var second).ShouldBeFalse();
        second.ShouldBeNull();
        hook.TryProcessKey(VK_LSHIFT, down: false, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LCONTROL, down: false, out _).ShouldBeFalse();

        hook.SetSuspended(false);
        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT, down: true, out var resumed).ShouldBeFalse();
        resumed!.Kind.ShouldBe(HotkeyEventKind.HoldDown);
    }
```

- [ ] **Step 9: Run the full non-Windows Platform test suite to verify green**

Run:
```bash
dotnet test tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj \
  -f net9.0 --filter "Platform!=Windows"
```
Expected: **PASS.** `Passed! - Failed: 0`. This includes
`ModifierPassthroughTests`, the reconciled `HotkeyHookLogicTests`,
`HotkeyChordTests`, `ChordRecorderTests`, and `HotkeyConflictsTests`.

- [ ] **Step 10: Commit**

```bash
git add src/Winpepper.Platform/Hotkeys/HotkeyHook.cs \
        tests/Winpepper.Platform.Tests/Hotkeys/ModifierPassthroughTests.cs \
        tests/Winpepper.Platform.Tests/Hotkeys/HotkeyHookLogicTests.cs
git commit -m "$(cat <<'EOF'
feat: pass modifier keys through to Windows in hotkey hook

Modifier keys (Ctrl/Shift/Alt/Win) used in a hotkey were being swallowed
system-wide when they completed a modifier-only chord, disabling them
everywhere. Only swallow non-modifier trigger keys; modifier keys now fire
the hotkey event but always pass through to Windows.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 2: Exhaustive regression coverage across every modifier key

**Files:**
- Modify: `tests/Winpepper.Platform.Tests/Hotkeys/ModifierPassthroughTests.cs`
  (add one parameterized `[Theory]`)

**Interfaces:**
- Consumes: `HotkeyHook.TryProcessKey` (pass-through contract from Task 1) and
  the modifier VK constants `VK_LCONTROL, VK_RCONTROL, VK_LSHIFT, VK_RSHIFT,
  VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN` (all `public const int` in
  `KeyboardHookNative`, usable as `[InlineData]` compile-time constants) and
  the `NewHook(string hold, ...)` helper already defined in the file in Task 1.
- Produces: none (test-only).

**Why this task:** Task 1's logic tests only exercise Ctrl and Shift. Alt and
Win keys are never tested, so a future edit could reintroduce swallowing for
them undetected. This parameterized test asserts the invariant for all eight
modifier virtual keys, each as a single-modifier hold chord (the smallest
chord whose completing key is that exact modifier).

- [ ] **Step 1: Write the failing parameterized test**

Add this `[Theory]` method inside the `ModifierPassthroughTests` class in
`tests/Winpepper.Platform.Tests/Hotkeys/ModifierPassthroughTests.cs` (place it
after the existing `[Fact]` methods, before the closing brace):

```csharp
    [Theory]
    [InlineData("LeftCtrl", VK_LCONTROL)]
    [InlineData("RightCtrl", VK_RCONTROL)]
    [InlineData("LeftShift", VK_LSHIFT)]
    [InlineData("RightShift", VK_RSHIFT)]
    [InlineData("LeftAlt", VK_LMENU)]
    [InlineData("RightAlt", VK_RMENU)]
    [InlineData("LeftWin", VK_LWIN)]
    [InlineData("RightWin", VK_RWIN)]
    public void AnyModifierUsedAsHotkey_IsNeverSwallowed(string chord, int vk)
    {
        var hook = NewHook(hold: chord);

        // Whatever modifier the user configured, its key-down fires the hold
        // but must still reach Windows so the modifier keeps working.
        hook.TryProcessKey(vk, down: true, out var down).ShouldBeFalse();
        down!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        // The key-up is symmetric and also passes through.
        hook.TryProcessKey(vk, down: false, out var up).ShouldBeFalse();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
    }
```

- [ ] **Step 2: Run the theory to verify all eight cases pass**

Run:
```bash
dotnet test tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj \
  -f net9.0 --filter "FullyQualifiedName~ModifierPassthroughTests.AnyModifierUsedAsHotkey_IsNeverSwallowed"
```
Expected: **PASS.** `Passed! - Failed: 0, Passed: 8` (one per `[InlineData]`).

Note: this theory passes immediately because Task 1 already implemented the
pass-through behavior; it is a regression guard that would fail if the swallow
guard is ever removed or narrowed. To see it fail (RED confirmation), one can
temporarily revert either `IsModifierKey` guard from Task 1 — do not commit
that revert.

- [ ] **Step 3: Run the full non-Windows Platform test suite**

Run:
```bash
dotnet test tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj \
  -f net9.0 --filter "Platform!=Windows"
```
Expected: **PASS.** `Passed! - Failed: 0`.

- [ ] **Step 4: Commit**

```bash
git add tests/Winpepper.Platform.Tests/Hotkeys/ModifierPassthroughTests.cs
git commit -m "$(cat <<'EOF'
test: assert every modifier key passes through when used as a hotkey

Parameterized coverage over all eight modifier virtual keys (Ctrl/Shift/Alt/Win,
left and right) so the modifier pass-through invariant cannot silently regress
for Alt or Win.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

## Self-Review

**1. Spec coverage.**
- "Any key used as a modifier flows through to Windows" → Task 1 Steps 4–5
  guard both activation sites with `IsModifierKey`; proven by
  `HoldModifierOnlyChord_...`, `ToggleModifierOnlyChord_...` (Task 1 Step 1)
  and the exhaustive theory `AnyModifierUsedAsHotkey_IsNeverSwallowed`
  (Task 2). ✓
- "If Shift is configured as a modifier, pressing Shift must not be
  swallowed" → directly asserted for `RightShift`/`LeftShift` in Task 1 and
  every Shift/Ctrl/Alt/Win variant in Task 2. ✓
- "App must not block/disable keys just because they participate in hotkey
  handling" → `MixedChord_ModifiersPassThrough_TriggerKeyIsSwallowed` proves
  modifiers in a mixed chord pass through while the non-modifier trigger is
  (intentionally) swallowed. ✓
- "Investigate the current keyboard hook / hotkey handling and fix remaining
  swallowing of modifier keys (commit 772746f)" → Root Cause section traces
  the two swallow sites introduced/retained by that commit; the fix removes
  modifier swallowing at both. The autorepeat/suspend/key-up swallow paths are
  shown to be modifier-safe automatically (they only swallow keys present in
  `_swallowedKeys`, which now never holds a modifier VK). ✓

**1b. No silent deferrals.** No stubs, mocks, fake providers, or synthetic
seams are used. Every requirement is proven by a test asserting the real
`TryProcessKey` return value (the exact bool `HookCallback` uses to pass the
key to Windows via `CallNextHookEx`). The production path is
`HookCallback → TryProcessKey → (swallow?) CallNextHookEx`; `TryProcessKey` is
the genuine decision point, not a test double. No requirement is moved to
"future work". No UNRESOLVED COVERAGE GAP.

Note on test level: the actual `SetWindowsHookExW` installation is
Windows-only and already covered by the separate `[Trait("Platform","Windows")]`
integration tests; those are unchanged because this fix does not alter hook
installation, only the per-key swallow decision, which is fully exercised
cross-platform through `TryProcessKey`.

**2. Placeholder scan.** No "TBD"/"TODO"/"handle edge cases"/"similar to Task
N" placeholders. Every code step shows complete, copy-pastable code; every run
step gives an exact command and expected summary.

**3. Type consistency.** `TryProcessKey(int vk, bool down, out HotkeyEvent?
evt)`, `IsModifierKey(int vk)`, `ModifierForVirtualKey(int vk)`, `NewHook(string
hold, string toggle, string cancel)`, `HotkeyEventKind.{HoldDown,HoldUp,Toggle}`,
and the `VK_*` constants are used identically across both tasks and match the
existing source. The renamed test
`HoldChord_AutorepeatOfCompletingModifier_PassesThroughWithoutDuplicateEvent`
is a full-method replacement (no dangling reference to the old name). The
Task 1 helper `NewHook(string hold, ...)` in `ModifierPassthroughTests` is
defined in Task 1 Step 1 and reused by Task 2 Step 1 with matching signature.
