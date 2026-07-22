# Status Pill Visual Fixes Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Fix the recording status pill so only the dark capsule is visible
(no surrounding white rounded rectangle) and add a live 5-bar voice meter
that lights up with the input level while recording.

**Architecture:** Two independent visual changes to the WinUI 3 always-on-top
overlay window (`StatusPillWindow`). Change 1 hardens the Win32 rounded-region
clip so the window silhouette is exactly the capsule at any DPI; Change 2 adds
a compact vertical-bar meter driven by the existing 100 ms tick and the smoothed
`InputLevel`. The two testable "brains" — the region rectangle math and the
bars-lit-from-level mapping — are extracted into pure functions with Linux xUnit
tests; the Windows-only XAML/P-Invoke shell stays thin and is validated by a
Windows smoke checklist because it cannot render on Linux.

**Tech Stack:** .NET 9, C#, WinUI 3 (Windows App SDK), xUnit v3, Shouldly.

## Global Constraints

- **Test runner:** xUnit v3 in-process runner. Build each test project with
  `-c Release`, then run the built dll via `dotnet exec`. NEVER use `dotnet test`
  (VSTest host is unreliable here). (from `AGENTS.md`)
- **SDK:** .NET 9 (`global.json` pins `9.0.100`, `rollForward: latestFeature`).
  A provisioned SDK exists at `/home/dan/code/winpepper/.dotnet/dotnet`. `/.dotnet`
  is gitignored.
- **Linux runs the pure-managed subset only.** WinUI/NAudio/DPAPI code compiles
  and runs on Windows only; a green Linux run is necessary but NOT sufficient.
  (from `AGENTS.md`)
- **The full test suite must pass before pushing.** (from `AGENTS.md`)
- **Do NOT touch** the keyboard hook or packaging/installer code. (spec)
- **Preserve pill behavior:** click-through in normal states, clickability in
  PENDING, topmost re-assert, no focus stealing, and all existing states/
  animations (dot color per stage, thinking pulse, voice-level dot scale). (spec)
- **The pill's Windows-only source files** (`StatusPillWindow.xaml`,
  `StatusPillWindow.xaml.cs`, `Views/Native/ExtendedWindowStyle.cs`) are guarded
  by `#if WINDOWS` or live in the WinUI App project and are NOT compiled on Linux.
  Edits to them are reasoned, not Linux-compiled; they are gated by the Windows
  smoke checklist (Task 8).
- **Pure helper files** added for testing (`StatusPillRegionGeometry.cs`,
  `VoiceMeter.cs`) MUST contain no Windows/WinUI types and no `#if WINDOWS`
  guard, so they compile on both Linux (into test projects) and Windows.

### Environment setup (run once at the start of every task)

```bash
cd /home/dan/code/winpepper/.worktrees/status-pill-visual-fixes
export DOTNET=/home/dan/code/winpepper/.dotnet/dotnet
"$DOTNET" --version   # expect: 9.0.100
```

If `/home/dan/code/winpepper/.dotnet/dotnet` is missing, provision the .NET 9
SDK into a gitignored `./.dotnet` and point `$DOTNET` at it:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --version 9.0.100 --install-dir ./.dotnet
export DOTNET=$(pwd)/.dotnet/dotnet
"$DOTNET" --version   # expect: 9.0.100
```

### Establish the baseline BEFORE Task 1 (do this first, do not skip)

The two pure-managed test projects this plan touches are
`Winpepper.Core.Tests` and `Winpepper.Platform.Tests`. Record their current
green state so every later "run tests" step is compared against a known
baseline (main just reverted `CollapsePunctuationRuns` and the greedy-decode
cap, so those tests are gone by design — do not look for them).

```bash
cd /home/dan/code/winpepper/.worktrees/status-pill-visual-fixes
export DOTNET=/home/dan/code/winpepper/.dotnet/dotnet

"$DOTNET" build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0
"$DOTNET" exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll

"$DOTNET" build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0
"$DOTNET" exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll
```

Expected: both print an xUnit summary ending in `Failed: 0` (some `Passed: N`).
Write those two N values into the plan's task log / commit message for the
first task so the reviewer can confirm no regressions. Task 8 additionally runs
the whole non-Windows suite (all 9 test projects at `-f net9.0`) and records the
aggregate baseline (~770 passing / 0 failing at the time of writing — establish
the ACTUAL number, do not hard-code it).

---

## Root Cause Analysis — Change 1 (the white rounded rectangle)

You cannot render on Linux; this section is the reasoned diagnosis the fix is
built on. Read it before implementing Tasks 2–4 and 6.

**What is drawn today.** `StatusPillWindow.xaml` sets `Window.Content` to a
`Border Background=#FF202020 CornerRadius=24 Padding=16,8`. The Border stretches
to fill the window client area (default `Stretch` alignment). Because
`CornerRadius=24` (a *radius*; diameter 48 == the 48-DIP client height), the
Border paints a dark **capsule** and leaves its four rounded corners transparent.
Behind those transparent corners is the WinUI content island's default (light)
backdrop. Windows 11 additionally auto-rounds the top-level window's rectangular
outline. Net effect when the clip fails: a dark capsule sitting inside a light
("white") rounded rectangle — exactly the reported symptom.

**The intended clip.** `ExtendedWindowStyle.ApplyRoundedRegion` calls
`SetWindowRgn` with a `CreateRoundRectRgn` capsule so the OS only shows the
capsule pixels and trims the light corners. When this region matches the client
exactly, only the capsule is visible.

**Why the current region is fragile (the bugs this plan fixes):**

1. **Off-by-one overshoot.** `ExtendedWindowStyle.cs:134-140` builds the region
   with `right = left + width + 1` and `bottom = top + height + 1`.
   `CreateRoundRectRgn`'s right/bottom are *exclusive*, so the correct values are
   `left + width` and `top + height`. The `+1` extends the region one pixel past
   the client on the right and bottom, producing a 1-px un-rounded overhang
   (a square sliver) outside the capsule silhouette.
2. **Corner diameter comes from a DIP constant, not the real client height.**
   The diameter is passed in as `layout.CornerDiameter` (a DIP value scaled by
   `GetWindowDpi(_hwnd)` at call time). It happens to equal the client height at
   every DPI *today*, but it is derived independently of the measured client
   rect, so any DIP/px rounding drift or mixed-DPI move can make the corner
   radius disagree with the actual capsule height and leave a light band. A true
   capsule must use the **measured client height** as the corner diameter.
3. **Robustness of re-application.** The region must be (re)applied after every
   `ResizeClient`, on every `Show`, and whenever the client size changes. The
   fix routes all region application through `ApplyLayout` (already called on
   each show via `PositionBottomCenter`) and keeps `RemoveSystemBorder`
   (`DWMWA_BORDER_COLOR = NONE`) so DWM does not draw an extra border.

**The fix (root cause, not cosmetics).** Extract the region-rectangle math into
a pure, unit-tested function (`StatusPillRegionGeometry.Compute`) that (a) sizes
the region to *exactly* the client rect (no `+1` overshoot) and (b) uses
`min(clientWidth, clientHeight)` as the corner diameter so the ends are true
semicircles — a perfect capsule at any DPI. `ApplyRoundedRegion` is refactored to
call it, deriving the corner diameter from the measured client height instead of
the DIP constant. Because the region is computed from the *measured* client rect,
the wider pill from Change 2 is handled automatically.

**On-device contingency (documented, not a code task here).** If, on the user's
machine, the big light rounded rectangle persists even with an exact capsule
region — i.e. the compositor is not honoring the GDI window region for the
DirectComposition-rendered content — the escalation is to remove the light at
its source: set the window's `SystemBackdrop` to none and make the content
root's background transparent so the corner cutouts reveal nothing. The spec
explicitly prefers the region fix and warns against a compositor rabbit hole, so
this plan ships the region fix and gates acceptance on the Windows smoke
checklist (Task 8); the contingency is recorded there for the on-device
operator. This is not a deferral of the requirement — the production region fix
IS implemented and shipped in Tasks 2–4/6; the smoke checklist is its acceptance
evidence because the pixels only exist on Windows.

---

## File Structure

**New files:**
- `src/Winpepper.Core/ViewModels/VoiceMeter.cs` — pure bars-lit-from-level
  mapping (Change 2 brain). No UI, no Windows types.
- `src/Winpepper.App/Views/StatusPillRegionGeometry.cs` — pure rounded-region
  rectangle math (Change 1 brain). No WinUI/Win32 types, no `#if WINDOWS`.
- `tests/Winpepper.Core.Tests/ViewModels/VoiceMeterTests.cs` — Linux unit tests.
- `tests/Winpepper.Platform.Tests/WindowContext/StatusPillRegionGeometryTests.cs`
  — Linux unit tests (geometry file compiled in, mirroring `StatusPillLayout`).

**Modified files:**
- `tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj` — compile-in
  the new geometry file.
- `src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs` — refactor
  `ApplyRoundedRegion` to use `StatusPillRegionGeometry` and measured client
  height (Windows-only, reasoned).
- `src/Winpepper.App/Views/StatusPillWindow.xaml.cs` — update `ApplyLayout` call
  site; wire the meter into the tick + state transitions (Windows-only, reasoned).
- `src/Winpepper.App/Views/StatusPillWindow.xaml` — add the meter bars column
  (Windows-only, reasoned).
- `src/Winpepper.App/Views/StatusPillLayout.cs` — widen the pill for the meter
  and drop the now-unused `CornerDiameter` (pure; Linux-tested).
- `tests/Winpepper.Platform.Tests/WindowContext/StatusPillLayoutTests.cs` —
  update expectations for the new width and dropped `CornerDiameter`.

**Task order & dependencies:**
1. Task 1 (Linux) — `VoiceMeter` pure mapping + tests.
2. Task 2 (Linux) — `StatusPillRegionGeometry` pure math + tests.
3. Task 3 (Windows-only) — refactor `ApplyRoundedRegion` + `ApplyLayout` call
   site to use the geometry and measured-height capsule; stop using
   `CornerDiameter`.
4. Task 4 (Linux) — widen `StatusPillLayout` and remove the now-dead
   `CornerDiameter`; update layout tests.
5. Task 5 (Windows-only) — add meter bars to the XAML.
6. Task 6 (Windows-only) — wire the meter into the code-behind (tick + states).
7. Task 7 (Linux) — full non-Windows suite green; record aggregate baseline.
8. Task 8 (docs) — Windows smoke checklist committed for the on-device operator.

---

## Task 1: VoiceMeter pure bars-lit mapping (Change 2 brain)

**Files:**
- Create: `src/Winpepper.Core/ViewModels/VoiceMeter.cs`
- Test: `tests/Winpepper.Core.Tests/ViewModels/VoiceMeterTests.cs`

**Interfaces:**
- Consumes: nothing (pure).
- Produces: `Winpepper.Core.ViewModels.VoiceMeter.BarsLit(double level, int barCount) -> int`.
  Maps a smoothed input level (`0..1`, clamped) to how many meter bars are lit:
  `0` at silence, at least `1` for any audible level, up to `barCount` at full
  scale. Consumed by `StatusPillWindow` (Task 6). `barCount` is `5` in the UI.

> **C# TDD note:** In a compiled language the RED step is a *build failure*
> (the referenced type/method does not exist yet) — that is the analog of
> "function not defined". Confirm the failure names the missing symbol, then
> implement.

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Core.Tests/ViewModels/VoiceMeterTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public sealed class VoiceMeterTests
{
    [Theory]
    // silence -> no bars
    [InlineData(0.0, 5, 0)]
    [InlineData(-0.5, 5, 0)]   // negative clamps to 0
    // any audible level lights at least one bar
    [InlineData(0.01, 5, 1)]
    [InlineData(0.20, 5, 1)]   // ceil(0.20*5)=1
    [InlineData(0.21, 5, 2)]   // ceil(1.05)=2
    [InlineData(0.50, 5, 3)]   // ceil(2.5)=3
    [InlineData(0.80, 5, 4)]   // ceil(4.0)=4
    [InlineData(1.00, 5, 5)]   // full scale
    [InlineData(1.50, 5, 5)]   // above range clamps to barCount
    public void BarsLit_MapsLevelToBarCount(double level, int barCount, int expected)
        => VoiceMeter.BarsLit(level, barCount).ShouldBe(expected);

    [Fact]
    public void BarsLit_NeverExceedsBarCount()
        => VoiceMeter.BarsLit(0.99, 3).ShouldBe(3); // ceil(2.97)=3, capped at 3

    [Fact]
    public void BarsLit_RejectsNonPositiveBarCount()
        => Should.Throw<System.ArgumentOutOfRangeException>(() => VoiceMeter.BarsLit(0.5, 0));
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd /home/dan/code/winpepper/.worktrees/status-pill-visual-fixes
export DOTNET=/home/dan/code/winpepper/.dotnet/dotnet
"$DOTNET" build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0
```

Expected: BUILD FAILS with an error like
`error CS0103: The name 'VoiceMeter' does not exist in the current context`
(or `CS0246`). That is the RED state.

- [ ] **Step 3: Write minimal implementation**

Create `src/Winpepper.Core/ViewModels/VoiceMeter.cs`:

```csharp
using System;

namespace Winpepper.Core.ViewModels;

/// <summary>
/// Pure mapping from a smoothed input level (0..1) to how many discrete meter
/// bars should light in the status pill's voice meter. Silence lights zero
/// bars; any audible level lights at least one; full scale lights them all.
/// No UI, no timers — fully testable.
/// </summary>
public static class VoiceMeter
{
    /// <summary>
    /// Number of lit bars for <paramref name="level"/> over
    /// <paramref name="barCount"/> total bars. <paramref name="level"/> is
    /// clamped to 0..1. Returns 0 at (or below) silence, otherwise
    /// ceil(level * barCount) clamped to [1, barCount].
    /// </summary>
    public static int BarsLit(double level, int barCount)
    {
        if (barCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(barCount));

        if (level <= 0 || double.IsNaN(level))
            return 0;
        if (level > 1)
            level = 1;

        var lit = (int)Math.Ceiling(level * barCount);
        if (lit < 1) lit = 1;
        if (lit > barCount) lit = barCount;
        return lit;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
"$DOTNET" build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0
"$DOTNET" exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll
```

Expected: xUnit summary with the new `VoiceMeterTests` counted and `Failed: 0`
(total = baseline N + 11).

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/ViewModels/VoiceMeter.cs \
        tests/Winpepper.Core.Tests/ViewModels/VoiceMeterTests.cs
git commit -m "feat(core): add VoiceMeter.BarsLit level-to-bars mapping"
```

---

## Task 2: StatusPillRegionGeometry pure region math (Change 1 brain)

**Files:**
- Create: `src/Winpepper.App/Views/StatusPillRegionGeometry.cs`
- Modify: `tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj`
- Test: `tests/Winpepper.Platform.Tests/WindowContext/StatusPillRegionGeometryTests.cs`

**Interfaces:**
- Consumes: nothing (pure ints).
- Produces:
  - `Winpepper.App.Views.StatusPillRegionRect` — `readonly record struct` with
    `int Left, int Top, int Right, int Bottom, int CornerDiameter`.
  - `Winpepper.App.Views.StatusPillRegionGeometry.Compute(int windowLeft, int windowTop, int clientOriginX, int clientOriginY, int clientWidth, int clientHeight) -> StatusPillRegionRect`.
  Consumed by `ExtendedWindowStyle.ApplyRoundedRegion` (Task 3). `Right`/`Bottom`
  are the *exclusive* coordinates to pass to `CreateRoundRectRgn` (no `+1`
  overshoot). `CornerDiameter = min(clientWidth, clientHeight)` for a true
  capsule. `Left`/`Top` are the client-area offset within the window frame
  (`clientOrigin - windowOrigin`).

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Platform.Tests/WindowContext/StatusPillRegionGeometryTests.cs`:

```csharp
using Shouldly;
using Winpepper.App.Views;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public sealed class StatusPillRegionGeometryTests
{
    [Fact]
    public void Compute_FramelessWindow_RegionMatchesClientExactly()
    {
        // Frameless: client origin == window origin, so Left/Top are 0.
        var r = StatusPillRegionGeometry.Compute(
            windowLeft: 100, windowTop: 200,
            clientOriginX: 100, clientOriginY: 200,
            clientWidth: 300, clientHeight: 48);

        r.Left.ShouldBe(0);
        r.Top.ShouldBe(0);
        r.Right.ShouldBe(300);   // exclusive == clientWidth, NO +1 overshoot
        r.Bottom.ShouldBe(48);   // exclusive == clientHeight, NO +1 overshoot
        r.CornerDiameter.ShouldBe(48); // min(300,48) -> true capsule ends
    }

    [Fact]
    public void Compute_WithFrameOffset_UsesClientOffsetForLeftTop()
    {
        // Simulate an 8-px left frame and 30-px top frame.
        var r = StatusPillRegionGeometry.Compute(
            windowLeft: 0, windowTop: 0,
            clientOriginX: 8, clientOriginY: 30,
            clientWidth: 260, clientHeight: 48);

        r.Left.ShouldBe(8);
        r.Top.ShouldBe(30);
        r.Right.ShouldBe(8 + 260);
        r.Bottom.ShouldBe(30 + 48);
        r.CornerDiameter.ShouldBe(48);
    }

    [Theory]
    // Corner diameter tracks the SHORTER side so the capsule ends stay round.
    [InlineData(300, 48, 48)]
    [InlineData(450, 72, 72)]   // 150% DPI equivalent
    [InlineData(40, 60, 40)]    // taller than wide -> min is width
    public void Compute_CornerDiameterIsMinOfClientSides(
        int clientWidth, int clientHeight, int expectedDiameter)
    {
        var r = StatusPillRegionGeometry.Compute(
            windowLeft: 0, windowTop: 0,
            clientOriginX: 0, clientOriginY: 0,
            clientWidth: clientWidth, clientHeight: clientHeight);

        r.CornerDiameter.ShouldBe(expectedDiameter);
    }
}
```

- [ ] **Step 2: Add the compile-in include, then run the test to verify it fails**

Edit `tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj`. In the
existing `<ItemGroup>` that compiles in `StatusPillLayout.cs`, add the geometry
file so it changes from:

```xml
  <ItemGroup>
    <!-- Compile the pure production layout helper without loading the WinUI app assembly. -->
    <Compile Include="..\..\src\Winpepper.App\Views\StatusPillLayout.cs" Link="Production\StatusPillLayout.cs" />
  </ItemGroup>
```

to:

```xml
  <ItemGroup>
    <!-- Compile the pure production helpers without loading the WinUI app assembly. -->
    <Compile Include="..\..\src\Winpepper.App\Views\StatusPillLayout.cs" Link="Production\StatusPillLayout.cs" />
    <Compile Include="..\..\src\Winpepper.App\Views\StatusPillRegionGeometry.cs" Link="Production\StatusPillRegionGeometry.cs" />
  </ItemGroup>
```

Then:

```bash
cd /home/dan/code/winpepper/.worktrees/status-pill-visual-fixes
export DOTNET=/home/dan/code/winpepper/.dotnet/dotnet
"$DOTNET" build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0
```

Expected: BUILD FAILS with `error CS0246`/`CS0103` naming
`StatusPillRegionGeometry` / `StatusPillRegionRect` (RED). (The `<Compile
Include>` points at a file that does not exist yet, so the build cannot find the
type — that is the intended RED.)

- [ ] **Step 3: Write minimal implementation**

Create `src/Winpepper.App/Views/StatusPillRegionGeometry.cs`:

```csharp
using System;

namespace Winpepper.App.Views;

/// <summary>
/// The exclusive-coordinate rectangle and corner diameter for the pill's
/// rounded window region, in physical pixels. Right/Bottom are the exclusive
/// bounds to hand to CreateRoundRectRgn (so they equal the client width/height,
/// not width/height + 1). CornerDiameter is the ellipse diameter for the
/// rounded corners.
/// </summary>
public readonly record struct StatusPillRegionRect(
    int Left,
    int Top,
    int Right,
    int Bottom,
    int CornerDiameter);

/// <summary>
/// Pure geometry for the status pill's rounded window region. Kept free of any
/// Win32/WinUI types so it unit-tests on Linux. The window region must exactly
/// match the client rect (no overshoot) with a corner diameter equal to the
/// shorter client side, producing a true capsule silhouette at any DPI.
/// </summary>
public static class StatusPillRegionGeometry
{
    /// <summary>
    /// Compute the rounded-region rectangle. All inputs are physical pixels.
    /// <paramref name="windowLeft"/>/<paramref name="windowTop"/> come from
    /// GetWindowRect; <paramref name="clientOriginX"/>/<paramref name="clientOriginY"/>
    /// come from ClientToScreen(0,0); width/height come from GetClientRect.
    /// </summary>
    public static StatusPillRegionRect Compute(
        int windowLeft,
        int windowTop,
        int clientOriginX,
        int clientOriginY,
        int clientWidth,
        int clientHeight)
    {
        var left = clientOriginX - windowLeft;
        var top = clientOriginY - windowTop;
        var cornerDiameter = Math.Min(clientWidth, clientHeight);

        return new StatusPillRegionRect(
            Left: left,
            Top: top,
            Right: left + clientWidth,   // exclusive: exactly the client width
            Bottom: top + clientHeight,  // exclusive: exactly the client height
            CornerDiameter: cornerDiameter);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
"$DOTNET" build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0
"$DOTNET" exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll
```

Expected: xUnit summary with `StatusPillRegionGeometryTests` counted and
`Failed: 0` (total = Platform baseline + 5).

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.App/Views/StatusPillRegionGeometry.cs \
        tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj \
        tests/Winpepper.Platform.Tests/WindowContext/StatusPillRegionGeometryTests.cs
git commit -m "feat(app): add pure StatusPillRegionGeometry for capsule window region"
```

---

## Task 3: Refactor ApplyRoundedRegion to the exact-capsule geometry (Windows-only)

**Files:**
- Modify: `src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs:120-149`
- Modify: `src/Winpepper.App/Views/StatusPillWindow.xaml.cs:247-253`

**Interfaces:**
- Consumes: `StatusPillRegionGeometry.Compute(...)` and `StatusPillRegionRect`
  (Task 2).
- Produces: `ExtendedWindowStyle.ApplyRoundedRegion(IntPtr hwnd) -> bool`
  (the `int cornerDiameter` parameter is REMOVED; the corner diameter is now
  derived from the measured client height). Consumed by
  `StatusPillWindow.ApplyLayout` (this task) and unchanged callers.

> **Windows-only, reasoned change.** These files are not compiled on Linux
> (WinUI/Win32). Do not attempt to build the App on Linux (`dotnet build` hits
> the in-process markup-compiler task and fails — see `README.md`). Correctness
> is verified by the Windows smoke checklist (Task 8). Make the edits exactly as
> shown so the region uses the exclusive bounds and measured-height capsule from
> Task 2.

- [ ] **Step 1: Replace the body of `ApplyRoundedRegion`**

In `src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs`, replace the current
method (lines 120-149):

```csharp
    public static bool ApplyRoundedRegion(IntPtr hwnd, int cornerDiameter)
    {
        if (!GetWindowRect(hwnd, out var windowRect) ||
            !GetClientRect(hwnd, out var clientRect))
            return false;

        var clientOrigin = new POINT();
        if (!ClientToScreen(hwnd, ref clientOrigin))
            return false;

        var left = clientOrigin.X - windowRect.Left;
        var top = clientOrigin.Y - windowRect.Top;
        var width = clientRect.Right - clientRect.Left;
        var height = clientRect.Bottom - clientRect.Top;
        var region = CreateRoundRectRgn(
            left,
            top,
            left + width + 1,
            top + height + 1,
            cornerDiameter,
            cornerDiameter);
        if (region == IntPtr.Zero)
            return false;

        if (SetWindowRgn(hwnd, region, redraw: true) != 0)
            return true;

        DeleteObject(region);
        return false;
    }
```

with (note: `cornerDiameter` parameter removed; geometry now derives it from the
measured client height, and the region bounds are exact — no `+1`):

```csharp
    /// <summary>
    /// Clip the window to a true capsule that exactly matches its client rect.
    /// The corner diameter equals the shorter client side (the height for the
    /// wide pill), so the ends are full semicircles; the region bounds are the
    /// EXCLUSIVE client bounds (no +1 overshoot) so no un-rounded sliver leaks
    /// outside the capsule. Region is computed from the MEASURED client rect in
    /// physical pixels, so it is correct at any DPI and after any resize. Call
    /// after every ResizeClient and on every Show.
    /// </summary>
    public static bool ApplyRoundedRegion(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var windowRect) ||
            !GetClientRect(hwnd, out var clientRect))
            return false;

        var clientOrigin = new POINT();
        if (!ClientToScreen(hwnd, ref clientOrigin))
            return false;

        var geometry = Views.StatusPillRegionGeometry.Compute(
            windowLeft: windowRect.Left,
            windowTop: windowRect.Top,
            clientOriginX: clientOrigin.X,
            clientOriginY: clientOrigin.Y,
            clientWidth: clientRect.Right - clientRect.Left,
            clientHeight: clientRect.Bottom - clientRect.Top);

        var region = CreateRoundRectRgn(
            geometry.Left,
            geometry.Top,
            geometry.Right,
            geometry.Bottom,
            geometry.CornerDiameter,
            geometry.CornerDiameter);
        if (region == IntPtr.Zero)
            return false;

        if (SetWindowRgn(hwnd, region, redraw: true) != 0)
            return true;

        DeleteObject(region);
        return false;
    }
```

> Note: `ExtendedWindowStyle` is in namespace `Winpepper.App.Views.Native`, so
> the geometry type is referenced as `Views.StatusPillRegionGeometry`
> (`Winpepper.App.Views`). This compiles on Windows because the pure file is
> globbed into the App project by the SDK default `**/*.cs` include.

- [ ] **Step 2: Update the call site in `StatusPillWindow.ApplyLayout`**

In `src/Winpepper.App/Views/StatusPillWindow.xaml.cs`, change `ApplyLayout`
(lines 247-253) from:

```csharp
    private StatusPillPixelLayout ApplyLayout(AppWindow appWindow, uint dpi)
    {
        var layout = StatusPillLayout.ForDpi(dpi);
        appWindow.ResizeClient(new SizeInt32(layout.ClientWidth, layout.ClientHeight));
        ExtendedWindowStyle.ApplyRoundedRegion(_hwnd, layout.CornerDiameter);
        return layout;
    }
```

to (drop the `dpi`-derived corner-diameter argument; the region measures the
client itself):

```csharp
    private StatusPillPixelLayout ApplyLayout(AppWindow appWindow, uint dpi)
    {
        var layout = StatusPillLayout.ForDpi(dpi);
        appWindow.ResizeClient(new SizeInt32(layout.ClientWidth, layout.ClientHeight));
        ExtendedWindowStyle.ApplyRoundedRegion(_hwnd);
        return layout;
    }
```

- [ ] **Step 3: Verify no other caller passes `cornerDiameter`**

```bash
cd /home/dan/code/winpepper/.worktrees/status-pill-visual-fixes
grep -rn "ApplyRoundedRegion" src tests
```

Expected: exactly two references — the definition in `ExtendedWindowStyle.cs`
and the single call in `StatusPillWindow.xaml.cs` (now argument-free). No test
references it. If any other call passes a second argument, update it to the
no-arg form.

- [ ] **Step 4: Confirm the Linux suite is unaffected**

These files are Windows-only and not compiled on Linux; confirm the pure test
projects still build and pass (nothing should have changed for them yet):

```bash
export DOTNET=/home/dan/code/winpepper/.dotnet/dotnet
"$DOTNET" build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0
"$DOTNET" exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll
```

Expected: `Failed: 0`, same total as end of Task 2.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs \
        src/Winpepper.App/Views/StatusPillWindow.xaml.cs
git commit -m "fix(app): clip pill to exact capsule region (measured height, no overshoot)"
```

---

## Task 4: Widen the pill for the meter and drop dead CornerDiameter (pure)

**Files:**
- Modify: `src/Winpepper.App/Views/StatusPillLayout.cs`
- Modify: `tests/Winpepper.Platform.Tests/WindowContext/StatusPillLayoutTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `StatusPillPixelLayout(int ClientWidth, int ClientHeight, int BottomGap)`
  — the `CornerDiameter` field is REMOVED (now unused after Task 3).
  `StatusPillLayout.ForDpi(uint dpi)` unchanged in signature. `ClientWidthDip`
  becomes `300` (was `260`) to fit the 5-bar meter added in Tasks 5-6.
  Consumed by `StatusPillWindow.ApplyLayout` and `PositionBottomCenter`.

> **Why 300 DIP:** the meter is 5 bars × 3 DIP + 4 gaps × 3 DIP = 27 DIP plus one
> 10-DIP `Grid.ColumnSpacing` slot ≈ 37 DIP of new content. 260 → 300 leaves
> headroom while keeping the pill compact. The window region measures the client
> at runtime, so the wider client flows into the capsule automatically.

- [ ] **Step 1: Update the failing test first**

Edit `tests/Winpepper.Platform.Tests/WindowContext/StatusPillLayoutTests.cs`.
Replace the whole file with (drops the `CornerDiameter` column; new widths for
300-DIP base — `ScaleToPixels(300,dpi)`):

```csharp
using Shouldly;
using Winpepper.App.Views;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public sealed class StatusPillLayoutTests
{
    [Theory]
    // width = round(300*dpi/96), height = round(48*dpi/96), gap = round(48*dpi/96)
    [InlineData(96u, 300, 48, 48)]
    [InlineData(120u, 375, 60, 60)]
    [InlineData(144u, 450, 72, 72)]
    [InlineData(192u, 600, 96, 96)]
    public void ForDpi_ScalesClientAndBottomGapTogether(
        uint dpi,
        int expectedWidth,
        int expectedHeight,
        int expectedBottomGap)
    {
        var layout = StatusPillLayout.ForDpi(dpi);

        layout.ClientWidth.ShouldBe(expectedWidth);
        layout.ClientHeight.ShouldBe(expectedHeight);
        layout.BottomGap.ShouldBe(expectedBottomGap);
    }

    [Fact]
    public void ForDpi_RejectsAnInvalidDpi()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => StatusPillLayout.ForDpi(0));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd /home/dan/code/winpepper/.worktrees/status-pill-visual-fixes
export DOTNET=/home/dan/code/winpepper/.dotnet/dotnet
"$DOTNET" build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0
```

Expected: BUILD FAILS — the test constructs/asserts a 3-field
`StatusPillPixelLayout` and 300-based widths, but production still has the
4-field record and 260 base. Error names `CornerDiameter` (`CS0117`/`CS1729`)
or the width assertions differ. That is RED.

- [ ] **Step 3: Update `StatusPillLayout.cs`**

Replace `src/Winpepper.App/Views/StatusPillLayout.cs` with:

```csharp
namespace Winpepper.App.Views;

internal readonly record struct StatusPillPixelLayout(
    int ClientWidth,
    int ClientHeight,
    int BottomGap);

internal static class StatusPillLayout
{
    private const int DefaultDpi = 96;
    private const int ClientWidthDip = 300;   // widened from 260 for the voice meter
    private const int ClientHeightDip = 48;
    private const int BottomGapDip = 48;

    public static StatusPillPixelLayout ForDpi(uint dpi)
    {
        if (dpi == 0)
            throw new ArgumentOutOfRangeException(nameof(dpi));

        return new StatusPillPixelLayout(
            ScaleToPixels(ClientWidthDip, dpi),
            ScaleToPixels(ClientHeightDip, dpi),
            ScaleToPixels(BottomGapDip, dpi));
    }

    private static int ScaleToPixels(int dips, uint dpi) =>
        checked((int)(((long)dips * dpi + DefaultDpi / 2) / DefaultDpi));
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
"$DOTNET" build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0
"$DOTNET" exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll
```

Expected: `Failed: 0`. `StatusPillLayoutTests` passes with the new widths.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.App/Views/StatusPillLayout.cs \
        tests/Winpepper.Platform.Tests/WindowContext/StatusPillLayoutTests.cs
git commit -m "refactor(app): widen pill to 300 DIP for meter; drop unused CornerDiameter"
```

---

## Task 5: Add the voice-meter bars to the pill XAML (Windows-only)

**Files:**
- Modify: `src/Winpepper.App/Views/StatusPillWindow.xaml`

**Interfaces:**
- Consumes: nothing yet (bars are static markup; code-behind drives them in
  Task 6).
- Produces: named elements consumed by Task 6 —
  `MeterPanel` (StackPanel) and `Bar0`,`Bar1`,`Bar2`,`Bar3`,`Bar4`
  (`Microsoft.UI.Xaml.Shapes.Rectangle`). The meter occupies a new `Auto`
  grid column between the Dot (col 0) and the status text; `StatusText` moves to
  col 2 and `ElapsedText` to col 3. Default `Visibility="Collapsed"` and unlit
  fill `#33FFFFFF`.

> **Windows-only, reasoned change.** XAML is not compiled on Linux; verified via
> Task 8 smoke checklist. Graduated bar heights (8→20) give a meter silhouette;
> the code-behind recolors bars to the warm accent when lit.

- [ ] **Step 1: Replace the Grid contents to add the meter column**

Replace the `<Grid>...</Grid>` block in `src/Winpepper.App/Views/StatusPillWindow.xaml`
(currently lines 14-37) with:

```xml
        <Grid ColumnSpacing="10" VerticalAlignment="Center">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <Ellipse x:Name="Dot" Width="10" Height="10" Fill="#FFEF4444"
                     RenderTransformOrigin="0.5,0.5">
                <Ellipse.RenderTransform>
                    <ScaleTransform x:Name="DotScale" ScaleX="1.0" ScaleY="1.0" />
                </Ellipse.RenderTransform>
            </Ellipse>
            <!-- Live voice meter: 5 vertical bars, lit proportionally to
                 InputLevel by the 100ms tick while Recording. Collapsed in all
                 other states. Unlit bars are dim (#33FFFFFF); the code-behind
                 recolors lit bars to the warm accent. -->
            <StackPanel x:Name="MeterPanel"
                        Grid.Column="1"
                        Orientation="Horizontal"
                        Spacing="3"
                        VerticalAlignment="Center"
                        Visibility="Collapsed">
                <Rectangle x:Name="Bar0" Width="3" Height="8"  RadiusX="1.5" RadiusY="1.5" Fill="#33FFFFFF" />
                <Rectangle x:Name="Bar1" Width="3" Height="11" RadiusX="1.5" RadiusY="1.5" Fill="#33FFFFFF" />
                <Rectangle x:Name="Bar2" Width="3" Height="14" RadiusX="1.5" RadiusY="1.5" Fill="#33FFFFFF" />
                <Rectangle x:Name="Bar3" Width="3" Height="17" RadiusX="1.5" RadiusY="1.5" Fill="#33FFFFFF" />
                <Rectangle x:Name="Bar4" Width="3" Height="20" RadiusX="1.5" RadiusY="1.5" Fill="#33FFFFFF" />
            </StackPanel>
            <TextBlock x:Name="StatusText"
                       Grid.Column="2"
                       Foreground="White"
                       FontSize="13"
                       Text="Recording..."
                       TextTrimming="CharacterEllipsis" />
            <TextBlock x:Name="ElapsedText"
                       Grid.Column="3"
                       Foreground="#AAFFFFFF"
                       FontSize="12"
                       Text="0 ms" />
        </Grid>
```

- [ ] **Step 2: Sanity-check the XAML edit (no Linux compile available)**

```bash
cd /home/dan/code/winpepper/.worktrees/status-pill-visual-fixes
grep -n 'x:Name="MeterPanel"\|x:Name="Bar0"\|x:Name="Bar4"\|Grid.Column="2"\|Grid.Column="3"' \
     src/Winpepper.App/Views/StatusPillWindow.xaml
```

Expected: matches for `MeterPanel`, `Bar0`, `Bar4`, and `StatusText`/`ElapsedText`
now at columns 2 and 3. (This is a structural sanity check; real rendering is
verified in Task 8.)

- [ ] **Step 3: Commit**

```bash
git add src/Winpepper.App/Views/StatusPillWindow.xaml
git commit -m "feat(app): add 5-bar voice meter column to status pill XAML"
```

---

## Task 6: Drive the voice meter from the tick and states (Windows-only)

**Files:**
- Modify: `src/Winpepper.App/Views/StatusPillWindow.xaml.cs`

**Interfaces:**
- Consumes: `VoiceMeter.BarsLit(double, int)` (Task 1);
  `MeterPanel`, `Bar0..Bar4` (Task 5); existing `_vm.InputLevel`, `_vm.Stage`,
  `ApplyAnimationFrame`, `ResetPillVisual`, `OnVmChanged`.
- Produces: no new public API; internal `MeterBarCount` const, `_meterBars`
  array, `UpdateMeter(int lit)`, and `SetMeterVisible(bool)` helpers.

> **Windows-only, reasoned change.** Verified via Task 8. The meter lights only
> while `Recording`; it is collapsed (and cleared) in every other state, so the
> Thinking pulse, Pending/Error visuals, click-through, PENDING clickability, and
> topmost re-assert are all untouched. The existing DotScale voice animation is
> preserved (both the dot swell and the bars react to level).

- [ ] **Step 1: Add meter fields and the bar array (top of the class)**

In `src/Winpepper.App/Views/StatusPillWindow.xaml.cs`, add these usings near the
top with the other WinUI usings (if not already present):

```csharp
using Microsoft.UI.Xaml.Shapes;
using Winpepper.Core.ViewModels;
```

Then add fields inside the class, next to the existing private fields
(after `private PillAnimationMode _animMode = PillAnimationMode.None;`, line 24):

```csharp
    private const int MeterBarCount = 5;
    private Rectangle[] _meterBars = Array.Empty<Rectangle>();

    private static readonly SolidColorBrush MeterLitBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B)); // warm amber accent
    private static readonly SolidColorBrush MeterDimBrush =
        new(Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)); // dim unlit bar
```

- [ ] **Step 2: Capture the bars in the constructor**

In the constructor, immediately after `InitializeComponent();` (line 36), add:

```csharp
        _meterBars = new[] { Bar0, Bar1, Bar2, Bar3, Bar4 };
```

- [ ] **Step 3: Add the meter helper methods**

Add these two methods to the class (e.g. just below `ResetPillVisual`,
after line 190):

```csharp
    /// <summary>Light the first <paramref name="lit"/> bars, dim the rest.</summary>
    private void UpdateMeter(int lit)
    {
        for (var i = 0; i < _meterBars.Length; i++)
            _meterBars[i].Fill = i < lit ? MeterLitBrush : MeterDimBrush;
    }

    /// <summary>Show or hide the meter; hiding also clears all bars to dim.</summary>
    private void SetMeterVisible(bool visible)
    {
        MeterPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) UpdateMeter(0);
    }
```

- [ ] **Step 4: Light the meter on each Recording tick**

In `ApplyAnimationFrame`, extend the `VoiceLevel` case (lines 201-206) to also
drive the bars (keep the existing dot scale):

```csharp
            case PillAnimationMode.VoiceLevel:
                var scale = 1.0 + (_vm.InputLevel * 0.8); // 1.0 .. 1.8
                DotScale.ScaleX = scale;
                DotScale.ScaleY = scale;
                Dot.Opacity = 1.0;
                UpdateMeter(VoiceMeter.BarsLit(_vm.InputLevel, MeterBarCount));
                break;
```

- [ ] **Step 5: Toggle meter visibility on state transitions**

In `OnVmChanged`:

(a) In the `else` branch that handles the working stages (lines 153-171), after
`Dot.Fill = new SolidColorBrush(...)` is assigned and before `_tickTimer.Start();`,
add — the meter is visible ONLY while Recording:

```csharp
            SetMeterVisible(_vm.Stage == SessionStage.Recording);
```

(b) In `ResetPillVisual` (lines 185-190), add a meter clear so Pending/Idle/Error
(all of which call `ResetPillVisual`) hide the meter:

```csharp
    private void ResetPillVisual()
    {
        Dot.Opacity = 1.0;
        DotScale.ScaleX = 1.0;
        DotScale.ScaleY = 1.0;
        SetMeterVisible(false);
    }
```

> This covers every state: Recording → meter visible and lit each tick;
> Transcribing/CleaningUp/Injecting → `SetMeterVisible(false)` via the `else`
> branch's `_vm.Stage == Recording` check; PendingPaste/Idle/Error → meter
> cleared via `ResetPillVisual`.

- [ ] **Step 6: Static verification (no Linux compile for WinUI)**

```bash
cd /home/dan/code/winpepper/.worktrees/status-pill-visual-fixes
grep -n "VoiceMeter.BarsLit\|SetMeterVisible\|UpdateMeter\|_meterBars = new" \
     src/Winpepper.App/Views/StatusPillWindow.xaml.cs
```

Expected: the tick calls `VoiceMeter.BarsLit(...)`, the `else` branch and
`ResetPillVisual` call `SetMeterVisible(...)`, and the constructor initializes
`_meterBars`. Full behavior is validated in Task 8.

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.App/Views/StatusPillWindow.xaml.cs
git commit -m "feat(app): drive voice meter from InputLevel tick while recording"
```

---

## Task 7: Full non-Windows suite green + record aggregate baseline

**Files:**
- None (verification only).

**Interfaces:** none.

- [ ] **Step 1: Build and run every test project on the Linux TFM**

```bash
cd /home/dan/code/winpepper/.worktrees/status-pill-visual-fixes
export DOTNET=/home/dan/code/winpepper/.dotnet/dotnet

for proj in \
  Winpepper.Asr.Tests \
  Winpepper.Audio.Tests \
  Winpepper.Cleanup.Tests \
  Winpepper.Core.Tests \
  Winpepper.Corrections.Tests \
  Winpepper.History.Tests \
  Winpepper.Models.Tests \
  Winpepper.Platform.Tests \
  Winpepper.IntegrationTests ; do
  echo "=== $proj ===";
  "$DOTNET" build "tests/$proj/$proj.csproj" -c Release -f net9.0 || { echo "BUILD FAIL: $proj"; break; };
  "$DOTNET" exec "tests/$proj/bin/Release/net9.0/$proj.dll" || echo "TESTS FAILED: $proj";
done
```

Expected: every project prints an xUnit summary with `Failed: 0`. Sum the
`Passed:` counts and confirm it matches the baseline established before Task 1
PLUS the tests added by this plan (11 from `VoiceMeter`, 5 from
`StatusPillRegionGeometry`; `StatusPillLayoutTests` count unchanged — one column
dropped, same case count). If any project fails to build on the Linux TFM
because it is Windows-only, note it: the intended non-Windows subset is the set
that builds green today (the pre-Task-1 baseline defines it).

> If a project genuinely does not build on `-f net9.0` because it is a
> Windows-only project, it is out of the Linux subset by design — record which
> projects are in the green Linux subset in the commit message. Do not "fix" a
> Windows-only project to build on Linux.

- [ ] **Step 2: Commit the baseline record (empty commit if nothing changed)**

```bash
git commit --allow-empty -m "test: record green non-Windows suite baseline after pill visual fixes"
```

---

## Task 8: Windows smoke checklist for the on-device operator (docs)

**Files:**
- Modify: `scripts/smoke-windows.ps1` (append a checklist block as PowerShell
  comments) — OR if that file's format is not comment-friendly, create
  `docs/plans/2026-07-22-status-pill-smoke-checklist.md`. Prefer appending to the
  existing script so the smoke steps live with the other Windows smoke guidance.

**Interfaces:** none (documentation for manual on-device verification).

> **Why a checklist, not a test:** the pill only renders on Windows; the pixel
> outcomes (no white rectangle, capsule silhouette, meter motion) cannot be
> asserted on Linux. This checklist is the acceptance evidence for Change 1 and
> Change 2's visual results and preserves the "reason carefully, keep Windows
> code thin, end with a smoke checklist" mandate.

- [ ] **Step 1: Inspect the existing smoke script format**

```bash
cd /home/dan/code/winpepper/.worktrees/status-pill-visual-fixes
sed -n '1,40p' scripts/smoke-windows.ps1
```

Decide: append a `#`-commented checklist block to `scripts/smoke-windows.ps1`
(preferred), or create the standalone md file above if the script is not a good
host.

- [ ] **Step 2: Add the checklist**

Append this block (as PowerShell `#` comments) to `scripts/smoke-windows.ps1`,
or write it as the body of the standalone md file:

```text
Status Pill Visual Fixes — Windows smoke checklist (run on a Windows host)

Setup:
  - Build & run the app on Windows (Windows App SDK). Start a dictation so the
    pill appears at the bottom-center of the foreground window's monitor.

Change 1 — capsule silhouette (no white rounded rectangle):
  [ ] At 100% display scale: the pill is ONLY the dark #202020 capsule. No
      white/light rounded rectangle is visible around or behind it.
  [ ] At 150% display scale (Settings > Display > Scale = 150%, then relaunch):
      still capsule-only; the rounded ends are true semicircles; no light band,
      halo, or square sliver at the right/bottom edges.
  [ ] Move the foreground window to a second monitor with a DIFFERENT scale and
      dictate there: the pill is capsule-only on that monitor too.
  [ ] If a white/light rounded rectangle STILL appears at any scale, the GDI
      window region is not clipping the composited content on this machine.
      Escalation (documented, do NOT do blindly): set the window SystemBackdrop
      to none and the content root background to transparent so the corner
      cutouts reveal nothing. Re-verify capsule-only after that change.

Change 2 — live voice meter:
  [ ] While Recording, 5 vertical bars are visible between the dot and the
      "Recording..." text.
  [ ] Speaking louder lights MORE bars (up to all 5); silence leaves 0 lit
      (all dim #33FFFFFF); lit bars are warm amber (#F59E0B). The bars react in
      near-real-time (100ms tick).
  [ ] The meter is HIDDEN in Thinking (Transcribing/CleaningUp/Injecting),
      PendingPaste, Error, and Idle states.

Preserved behavior (regression guard):
  [ ] Normal states are click-through (clicks pass to the app underneath).
  [ ] In PendingPaste the pill is CLICKABLE and clicking pastes into the focused
      field; clicking never steals focus.
  [ ] The pill stays on top (create another topmost window; the pill re-asserts
      above it within a tick).
  [ ] Dot color per stage and the Thinking opacity pulse still work; the dot
      still swells with voice level while Recording.
```

- [ ] **Step 3: Commit**

```bash
git add scripts/smoke-windows.ps1   # or: git add docs/plans/2026-07-22-status-pill-smoke-checklist.md
git commit -m "docs: add Windows smoke checklist for status pill visual fixes"
```

---

## Self-Review

**1. Spec coverage.**

| Spec requirement | Covered by |
|---|---|
| Diagnose white-rect mechanism from code (no Linux render) | Root Cause Analysis section |
| Fix so ONLY capsule pixels visible; region matches Border bounds | Tasks 2, 3 (exact-bounds capsule region) |
| Region in physical px / DPI-scaled | Task 2 (measured client px) + Task 3 (measured GetClientRect) |
| Corner diameter = window height for a perfect capsule | Task 2 (`min(clientWidth, clientHeight)`) |
| Region re-applied after layout/DPI and on every Show | Task 3 (via `ApplyLayout`, called from `PositionBottomCenter` each show) |
| Window sized to the capsule; account for meter width | Task 4 (300 DIP) + region measures client at runtime |
| Prefer simplest correct fix; document why not compositor | Root Cause Analysis contingency + Task 8 escalation |
| Preserve click-through / PENDING click / topmost / no focus steal / states/animations | Tasks 3 & 6 touch only region + meter; Task 8 regression guard |
| Live meter visible during Recording (~5 bars, proportional) | Tasks 5, 6 |
| Reuse existing 100ms tick + InputLevel, no new audio plumbing | Task 6 (`ApplyAnimationFrame` VoiceLevel case) |
| Warm accent lit bars; dim unlit #33FFFFFF | Tasks 5 (dim) & 6 (amber lit) |
| Hide/blank meter in non-Recording states | Task 6 (`else` branch gate + `ResetPillVisual` clear) |
| Bars-lit mapping = pure function + unit test | Task 1 (`VoiceMeter.BarsLit`) |
| Keep XAML thin | Tasks 5 (static markup) & 6 (logic in code-behind + pure fn) |
| Pure-managed xUnit v3 tests on Linux via `dotnet exec` | Env setup + Tasks 1,2,4,7 |
| Establish actual baseline first | "Establish the baseline BEFORE Task 1" + Task 7 |
| Run full non-Windows suite | Task 7 |
| Windows smoke checklist (100%/150% DPI, capsule, meter, pending/click-through/topmost) | Task 8 |
| Do NOT touch keyboard hook or packaging | Global Constraints; no task touches them |

**1b. No silent deferrals of required behavior.** The two user-facing visual
outcomes (no white rectangle; meter moves with voice) render only on Windows, so
their production code (region P/Invoke in Task 3; XAML+code-behind in Tasks 5-6)
is fully implemented — no stubs, mocks, fakes, or TODOs stand in for it. Their
acceptance evidence is the Windows smoke checklist (Task 8), because the pixels
physically cannot exist on the Linux runner (the spec states this explicitly).
The testable brains of both changes (region rectangle math, bars-lit mapping)
ARE proven by Linux unit tests (Tasks 1, 2). No requirement is moved to "known
limitations" or "future work." The only conditional item is the on-device
compositor contingency, which is an *escalation path for the same requirement*,
not a reduction of it — the region fix ships and is the primary implementation.
No UNRESOLVED COVERAGE GAP remains.

**2. Placeholder scan.** No "TBD"/"add error handling"/"similar to Task N"/
"write tests for the above" placeholders. Every code step shows complete code;
every command shows expected output; the RED steps state the exact expected
compile/build failure.

**3. Type consistency.** Verified across tasks:
- `VoiceMeter.BarsLit(double level, int barCount) -> int` — defined Task 1,
  called Task 6 as `VoiceMeter.BarsLit(_vm.InputLevel, MeterBarCount)`. Match.
- `StatusPillRegionGeometry.Compute(int,int,int,int,int,int) -> StatusPillRegionRect`
  with fields `Left,Top,Right,Bottom,CornerDiameter` — defined Task 2, called
  Task 3 with named args matching. Match.
- `ApplyRoundedRegion(IntPtr hwnd)` (no `cornerDiameter`) — redefined Task 3,
  called once in `ApplyLayout` (Task 3) argument-free. Grep step confirms no
  stale two-arg caller. Match.
- `StatusPillPixelLayout(int ClientWidth, int ClientHeight, int BottomGap)` —
  `CornerDiameter` removed Task 4; last production use of `layout.CornerDiameter`
  removed in Task 3 (before the field is deleted), so no dangling reference.
  Layout test updated in the same task (Task 4). Match.
- XAML names `MeterPanel`, `Bar0..Bar4` (Task 5) consumed by `_meterBars` array
  and `MeterPanel.Visibility` (Task 6). Match.

No issues found beyond those already resolved inline.
