# Injection Delivery-Strategy Ladder Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Replace TextInjector's single hardcoded SendInput path with a ladder of
pinned, swappable delivery strategies (EM_REPLACESEL → WM_CHAR/SMTO → VK_PACKET),
routed once per run by capability gates, with provenance telemetry — per
`docs/designs/2026-08-06-injection-delivery-integrity-v3.md` (v3.2, Accepted; it is
the source of truth where anything here is ambiguous).

**Architecture:** A new `IDeliveryStrategy` contract with three rung implementations
lives in `src/Winpepper.Platform/Injection/`. At send start, TextInjector
double-samples the focused child window (`GetGUIThreadInfo.hwndFocus`), walks a
settings-ordered ladder once, and hands the winning strategy's `TrySendChunk` as the
`sendChunk` delegate into the **unchanged** `GuardedInjectionRun` loop. All chunking,
pacing, halts, elevation handling, and the pending-paste/pill flows are untouched.
`InjectionRunReport` gains `Via`/`GatesSummary`; the dictation timing line gains
`inject_via=`/`inject_gates=`.

**Tech Stack:** C# / .NET 9, `LibraryImport` P/Invoke (source-generated), xUnit v3
in-process runner + Shouldly, System.Text.Json settings.

## Global Constraints

- Repo root (the isolated worktree — ALL work happens here):
  `/home/dan/code/winpepper/.worktrees/injection-delivery-ladder`
- Before EVERY commit: `./scripts/linux-tests.sh` must print `LINUX SUITE: GREEN`
  (all 9 projects, 0 failures). Never use `dotnet test` — always
  `dotnet build ... -c Release -f net9.0 -p:EnableWindowsTargeting=true` then
  `dotnet exec <built dll>` (xUnit v3 in-process runner).
- Before anything is pushed/merged: `./scripts/windows-gate.sh` from WSL must print
  `GATE: GREEN` (use a 20–40 min timeout). The merge itself happens from the main
  checkout AFTER this workflow completes; do not push from the worktree.
- `src/Winpepper.Platform/Injection/GuardedInjectionRun.cs` must be **byte-identical
  to main** at the end (`git diff main -- <file>` empty). Its 14 existing tests in
  `GuardedInjectionRunTests.cs` must remain untouched and green.
- Do NOT implement a fenced/posted WM_CHAR strategy (falsified, E9b) and do NOT
  implement any clipboard strategy (owner decision; clipboard exists only in the
  unchanged click-to-copy failure pill).
- Pinned Win32 values: `EM_GETSEL = 0x00B0`, `EM_REPLACESEL = 0x00C2` (wParam=1),
  `WM_CHAR = 0x0102` (lParam=1, one message per UTF-16 code unit),
  `SMTO_ABORTIFHUNG = 0x0002`, SMTO timeout **150 ms**, double-sample gap **30 ms**.
- Canonical channel name strings (settings + telemetry): `emReplaceSel`,
  `wmCharSmto`, `vkPacket` (the design doc's `wmCharFenced`/`wmchar` spellings in
  §2.4/§3 examples are doc typos; `wmCharSmto` is canonical per its §2.2/§2.3).
- Unchanged invariants: `ChunkCodeUnits = 8`, `InterChunkPauseMs = 14`, deadline
  pacer, per-chunk foreground/modifier/mouse halts, elevation park,
  `NeutralizeHeldModifiers`, pending-paste/pill flows, `InjectionText.ForPaste`.
- Code style: file-scoped namespaces, `LibraryImport` (never `DllImport`),
  `internal static partial class *Native` P/Invoke surfaces, Shouldly assertions,
  `NullLogger<T>.Instance` in tests, `[Trait("Platform", "Windows")]` +
  `if (!OperatingSystem.IsWindows()) return;` for Windows-only tests.
- No new end-user markdown docs. The only doc change allowed is appending an
  implementation-notes section to the design doc (Task 12).
- Scope guardrails: no ASR/cleanup/silence-gate/history/UI changes beyond the
  telemetry stamps; no config UI (settings.json field only).

**Scope check:** this is one subsystem (injection delivery) plus its settings knob
and telemetry stamps — one plan. Tasks 1–7 produce a fully working, Linux-tested
ladder inside Winpepper.Platform; Tasks 8–10 wire settings + telemetry; Task 11 adds
the Windows in-proc proof; Task 12 closes out.

## Pinned implementation decisions (spec gaps resolved here, not by implementers)

The design doc leaves a few things unstated. These decisions are FINAL for this
plan; Task 12 appends them to the design doc:

1. **`DeliveryChannel` enum values:** `VkPacket = 0`, `EmReplaceSel = 1`,
   `WmCharSmto = 2`. VkPacket must be `0` so `default(InjectionRunReport).Via`
   is `VkPacket` (the spec requires "default VkPacket", and
   `PipelineHost.TryPastePending` uses `InjectionRunReport report = default;`).
2. **Stability plumbing:** the capture produces
   `FocusedChildCapture(long FocusedChildHwnd, bool Stable)` with the invariant
   *not-Stable ⇒ FocusedChildHwnd == 0*. `CanDeliver(foregroundHwnd,
   focusedChildHwnd)` receives that **effective** hwnd — `0` when unstable/zero —
   which is how the pinned two-`long` interface "receives the stability fact".
   Rungs 1–2 gate on `focusedChildHwnd != 0`.
3. **Gate-out reason vocabulary is owned by the router** (the pinned interface
   returns only `bool`): a rung that gates out is recorded as
   `<channelName>:focus-unstable` when the capture was unstable/zero, else
   `<channelName>:no-em` (covers rung 1's class-mismatch and EM_GETSEL-probe
   failure — the only stable-focus gate-out that exists).
4. **Ladder exhaustion falls back to the VkPacket floor:** if settings removed
   `vkPacket` and every configured rung gates out, deliver via the VkPacket
   strategy anyway (design doc §3/E9e: "rungs degrade to the VK_PACKET floor =
   status quo"); the gated-out rungs are still recorded in `GatesSummary`.
5. **Render position:** `inject_via=` immediately after `inject_chunks=`, and
   `inject_gates=` immediately after `inject_via=` (both before `inject_pace=`);
   `inject_gates` omitted when null/empty.
6. **Stamping guard:** `InjectVia`/`InjectGates` are stamped inside the existing
   `if (injReport.ChunksTotal > 0)` guard — via/gates are provenance of an
   *attempted delivery*, exactly like `inject_chunks`.
7. **`TryPastePending` does not report timings** (verified: it has no
   `DictationTimingSummary`), so its "stamp" is extending its existing
   "Pending paste injected" log line with `via <channel>`.
8. **Capture placement:** the double-sample + ladder walk run after the existing
   elevation check AND after the modifier/mouse release preludes, immediately
   before chunking — "after the existing elevation check" per the spec, and as
   close to the first send as possible (the preludes can take up to 1.5 s during
   which focus legitimately settles).
9. **Probe short-circuit:** if the FIRST focused-child sample is `0`, the capture
   returns unstable immediately without the 30 ms gap or second sample (the
   outcome is already determined; behavior-equivalent to sampling twice).
10. The design doc's "rung 4" references are a typo for rung 3 (`VkPacket`) — its
    own §2.2 table defines exactly 3 rungs with VkPacket as the status-quo floor.
11. **Duplicate channel names in settings are de-duplicated** (first occurrence
    wins) so gates run at most once per rung per run.

## Known residual risks (surfaced by load-bearing validation, 2026-08-07)

A load-bearing validation pass confirmed the plan's technical pins (Win32
constants/semantics, both `LibraryImport` P/Invoke forms compile on net9.0, no
`AppSettings` value-equality blast radius) and surfaced four safety facts the
design doc and this plan were previously **silent** on. None changes the accepted
v3.2 architecture and none is a stop-the-line data-loss risk (every blast radius
below is a visible, same-application, user-recoverable text anomaly — never silent
corruption of unrelated data or an irreversible action). They are recorded here so
implementers and the owner treat them as *known* rather than *discovered-in-the-field*.
Each was evaluated against alternatives (per-chunk hardening vs. document-and-defer);
the accepted-design deferral won because in-repo code mitigations either expand past
the accepted fixed-target model, add per-chunk Win32 calls + latency, or perturb the
byte-identical `GuardedInjectionRun` machinery — disproportionate to low-probability
edges that the design already routes to field telemetry.

- **R1 — Stale focused-child target within one foreground window (from A2).**
  Rungs 1–2 capture the focused-child hwnd ONCE and `SendMessageTimeout` every chunk
  to that fixed hwnd. The unchanged `GuardedInjectionRun`/`MidPasteDecider` halt
  observes only the **top-level foreground window**, never the focused *child*. If the
  user Tabs/clicks to a *different child control inside the same still-foreground
  window* mid-run, rungs 1–2 keep delivering to the stale child and the halt never
  fires (rung 3/SendInput cannot exhibit this — it has no fixed target). Accepted as
  residual: the fixed-target model is the pinned v3.2 design; a per-chunk focused-child
  re-check was considered and deferred (adds a Win32 sample + latency per chunk and
  expands the strategy contract). Watched via the owner field review (Task 12 step 5).
- **R2 — Timed-out SMTO send may still be processed later (from A3).**
  A `SendMessageTimeout(SMTO_ABORTIFHUNG, 150 ms)` returning `false` does **not**
  guarantee non-delivery: the OS hang heuristic is ≥5 s of no `GetMessage`, so a merely
  *slow* (not hung) receiver can dequeue and process the "timed-out" chunk later
  (confirmed against MS Learn). The **automated** path stays safe because the pinned
  no-reroute / no-auto-retry behavior (a first refused chunk ⇒ `SendFailed` ⇒ pill,
  never a second automated send) is load-bearing — **do not add auto-retry or reroute
  on `SendFailed` for a message-based rung without content de-duplication.** Residual:
  a *manual* user retry after the pill could duplicate text (visible, correctable).
- **R3 — Rung-2 widens the per-chunk exposure window (from A4).**
  Rung 2 sends one `SendMessageTimeout` per UTF-16 unit; a chunk (`ChunkCodeUnits = 8`,
  9 across a surrogate straddle) is up to 8–9 × 150 ms ≈ **~1.35 s worst case**, whereas
  `GuardedInjectionRun`'s bleed-safety comment assumes "the exposure window is the
  (microsecond-scale) send itself" and halts run only at chunk boundaries. The worst case
  only arises against a slow/degraded target (which would fail SMTO and stop shortly
  after); healthy targets process each WM_CHAR in ~µs. Accepted as the rung-2
  correctness/liveness trade-off already priced in design §5.2; intra-chunk halts were
  considered and deferred (would perturb the byte-identical run loop).
- **R4 — Real-target correctness & capture are gate/owner-verified, not Linux-proven
  (from A1/A5/A6).** The Linux suite proves only off-Windows fail-closed behavior and
  pure logic; Task 11 hosts a synthetic classic `EDIT`, not the motivating classes.
  Rung-1's "class contains 'edit'" gate correctly routes Chromium (`Chrome_WidgetWin_1`)
  and Terminal (`InputSite.WindowClass`) AWAY from EM_REPLACESEL, so rung-1 correctness
  need only hold for genuine edit controls (E9a-tested, n=4); Chromium/Terminal ride
  rung 2 (WM_CHAR), probed at n=1–2. The "edit"-substring gate and cross-process /
  elevated-target `GetGUIThreadInfo` capture (graceful-degrades to the VkPacket floor if
  it fails) are inductive/edge claims with no cheaper proof than the Windows gate +
  owner live runs + `inject_via`/`inject_gates` telemetry. Enumerated in Task 12 step 5.

## File structure

**Create — `src/Winpepper.Platform/Injection/` (namespace `Winpepper.Platform.Injection`):**

| File | Responsibility |
|---|---|
| `DeliveryChannel.cs` | `public enum` of the three channels |
| `InjectionChannelNames.cs` | `public static` name constants, case-insensitive parse, ladder-order parsing with warn-and-skip + default fallback |
| `FocusedChildCapture.cs` | `public readonly record struct` capture result |
| `FocusedChildProbe.cs` | `internal static` pure double-sample logic (seamed sampler + sleep) |
| `MessageDeliveryNative.cs` | `internal static partial` P/Invoke surface: `GetGUIThreadInfo`, `GetClassNameW`, `SendMessageTimeoutW` ×2 + structs + constants |
| `MessageDelivery.cs` | `internal static` managed wrappers over the native surface, `OperatingSystem.IsWindows()`-guarded |
| `IDeliveryStrategy.cs` | the pinned `internal interface` |
| `EmReplaceSelStrategy.cs` | rung 1 |
| `WmCharSmtoStrategy.cs` | rung 2 |
| `VkPacketStrategy.cs` | rung 3 (wraps the existing send delegate) |
| `DeliveryLadder.cs` | `internal static` router + `DeliverySelection` result struct |

**Modify:**
- `src/Winpepper.Platform/Injection/InjectionRunReport.cs` — add `Via`, `GatesSummary`
- `src/Winpepper.Platform/Injection/TextInjector.cs` — new seams + routing
- `src/Winpepper.Core/Settings/AppSettings.cs` — `InjectionChannels`
- `src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs` — list-aware diff
- `src/Winpepper.Core/Winpepper.Core.csproj` — add `InternalsVisibleTo` for Core.Tests
- `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs` — `InjectVia`/`InjectGates`
- `src/Winpepper.App/Hosting/PipelineHost.cs` — ctor wiring + stamps
- `docs/designs/2026-08-06-injection-delivery-integrity-v3.md` — append-only notes

**Tests create (namespace `Winpepper.Platform.Tests.Injection` unless noted):**
`InjectionChannelNamesTests.cs`, `FocusedChildProbeTests.cs`,
`MessageDeliveryTests.cs`, `EmReplaceSelStrategyTests.cs`,
`WmCharSmtoStrategyTests.cs`, `VkPacketStrategyTests.cs`, `DeliveryLadderTests.cs`,
`TextInjectorLadderTests.cs`, `NativeEditHost.cs` (helper),
`DeliveryStrategyWindowsTests.cs` — all under
`tests/Winpepper.Platform.Tests/Injection/`; plus
`tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterDiffTests.cs`.

**Tests modify:**
`tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs` (mechanical),
`tests/Winpepper.Core.Tests/AppSettingsDefaultsTests.cs`,
`tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs`,
`tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs`.

## Shared test commands

Every task uses these; they are referenced as **[BUILD-PLATFORM]**, **[RUN-CLASS]**,
**[BUILD-CORE]**, **[RUN-CORE]**, **[FULL-SUITE]**:

```bash
cd /home/dan/code/winpepper/.worktrees/injection-delivery-ladder
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
export PATH="$DOTNET_ROOT:$PATH"

# [BUILD-PLATFORM]
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true

# [RUN-CLASS] (substitute the class name)
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll \
  -class "Winpepper.Platform.Tests.Injection.<ClassName>" -notrait "Platform=Windows"

# [BUILD-CORE]
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true

# [RUN-CORE] (substitute the class name)
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll \
  -class "<FullyQualifiedClassName>" -notrait "Platform=Windows"

# [FULL-SUITE] — REQUIRED green before every commit
./scripts/linux-tests.sh
```

A new test class fails at the `dotnet exec` step with a compile error in the build
step instead when its subject type doesn't exist yet — for these steps "Expected:
FAIL" means *build fails with CS0246 (type not found)*, which is the RED step of the
cycle. Run the build anyway to observe it.

---

### Task 1: DeliveryChannel enum + InjectionChannelNames (names, parse, default ladder)

**Files:**
- Create: `src/Winpepper.Platform/Injection/DeliveryChannel.cs`
- Create: `src/Winpepper.Platform/Injection/InjectionChannelNames.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/InjectionChannelNamesTests.cs`

**Interfaces:**
- Consumes: nothing (leaf task).
- Produces: `public enum DeliveryChannel { VkPacket = 0, EmReplaceSel = 1, WmCharSmto = 2 }`;
  `public static class InjectionChannelNames` with
  `const string EmReplaceSelName/WmCharSmtoName/VkPacketName`,
  `static readonly IReadOnlyList<DeliveryChannel> DefaultLadder`,
  `string Name(DeliveryChannel)`, `bool TryParse(string?, out DeliveryChannel)`,
  `IReadOnlyList<DeliveryChannel> ParseLadder(IReadOnlyList<string>?, Action<string>? onUnknown = null)`.
  Used by Tasks 6, 7, 9-tests, 10.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Platform.Tests/Injection/InjectionChannelNamesTests.cs`:

```csharp
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class InjectionChannelNamesTests
{
    [Theory]
    [InlineData("emReplaceSel", DeliveryChannel.EmReplaceSel)]
    [InlineData("EMREPLACESEL", DeliveryChannel.EmReplaceSel)]
    [InlineData("wmcharsmto", DeliveryChannel.WmCharSmto)]
    [InlineData("WmCharSmto", DeliveryChannel.WmCharSmto)]
    [InlineData("vkPacket", DeliveryChannel.VkPacket)]
    [InlineData("VKPACKET", DeliveryChannel.VkPacket)]
    public void TryParse_IsCaseInsensitive(string name, DeliveryChannel expected)
    {
        InjectionChannelNames.TryParse(name, out var channel).ShouldBeTrue();
        channel.ShouldBe(expected);
    }

    [Theory]
    [InlineData("clipboard")]
    [InlineData("wmCharFenced")] // falsified rung (E9b) must NOT parse
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_RejectsUnknownNames(string? name)
    {
        InjectionChannelNames.TryParse(name, out _).ShouldBeFalse();
    }

    [Fact]
    public void Name_RoundTrips_AllChannels()
    {
        InjectionChannelNames.Name(DeliveryChannel.EmReplaceSel).ShouldBe("emReplaceSel");
        InjectionChannelNames.Name(DeliveryChannel.WmCharSmto).ShouldBe("wmCharSmto");
        InjectionChannelNames.Name(DeliveryChannel.VkPacket).ShouldBe("vkPacket");
    }

    [Fact]
    public void DefaultLadder_IsPinnedOrder()
    {
        InjectionChannelNames.DefaultLadder.ShouldBe(new[]
        {
            DeliveryChannel.EmReplaceSel,
            DeliveryChannel.WmCharSmto,
            DeliveryChannel.VkPacket,
        });
    }

    [Fact]
    public void ParseLadder_HonorsConfiguredOrder()
    {
        var order = InjectionChannelNames.ParseLadder(new[] { "vkPacket", "emReplaceSel" });
        order.ShouldBe(new[] { DeliveryChannel.VkPacket, DeliveryChannel.EmReplaceSel });
    }

    [Fact]
    public void ParseLadder_UnknownNames_AreReportedAndSkipped()
    {
        var unknown = new List<string>();
        var order = InjectionChannelNames.ParseLadder(
            new[] { "clipboard", "wmCharSmto" }, unknown.Add);
        order.ShouldBe(new[] { DeliveryChannel.WmCharSmto });
        unknown.ShouldBe(new[] { "clipboard" });
    }

    [Fact]
    public void ParseLadder_NullEmptyOrAllInvalid_FallsBackToDefault()
    {
        InjectionChannelNames.ParseLadder(null).ShouldBe(InjectionChannelNames.DefaultLadder);
        InjectionChannelNames.ParseLadder(Array.Empty<string>()).ShouldBe(InjectionChannelNames.DefaultLadder);
        InjectionChannelNames.ParseLadder(new[] { "bogus" }).ShouldBe(InjectionChannelNames.DefaultLadder);
    }

    [Fact]
    public void ParseLadder_Duplicates_KeepFirstOccurrenceOnly()
    {
        var order = InjectionChannelNames.ParseLadder(new[] { "vkPacket", "vkPacket", "emReplaceSel" });
        order.ShouldBe(new[] { DeliveryChannel.VkPacket, DeliveryChannel.EmReplaceSel });
    }

    [Fact]
    public void DeliveryChannel_Default_IsVkPacket()
    {
        // Pinned: VkPacket == 0 so default(InjectionRunReport).Via is the
        // status-quo floor (design decision #1 in the plan).
        default(DeliveryChannel).ShouldBe(DeliveryChannel.VkPacket);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run **[BUILD-PLATFORM]**. Expected: FAIL with CS0246 (`DeliveryChannel`,
`InjectionChannelNames` not found).

- [ ] **Step 3: Implement**

Create `src/Winpepper.Platform/Injection/DeliveryChannel.cs`:

```csharp
namespace Winpepper.Platform.Injection;

/// <summary>
/// Injection delivery channels (design doc 2026-08-06 §2.2). VkPacket is
/// deliberately 0 so a default(InjectionRunReport).Via reads as the
/// status-quo floor. Ladder order is NOT this declaration order — it comes
/// from settings via InjectionChannelNames.ParseLadder.
/// </summary>
public enum DeliveryChannel
{
    /// <summary>Rung 3: today's SendInput KEYEVENTF_UNICODE path (status-quo floor; gate always passes).</summary>
    VkPacket = 0,

    /// <summary>Rung 1: one EM_REPLACESEL per chunk via SendMessageTimeout (edit-class targets).</summary>
    EmReplaceSel = 1,

    /// <summary>Rung 2: one WM_CHAR per UTF-16 code unit via SendMessageTimeout (SMTO_ABORTIFHUNG).</summary>
    WmCharSmto = 2,
}
```

Create `src/Winpepper.Platform/Injection/InjectionChannelNames.cs`:

```csharp
namespace Winpepper.Platform.Injection;

/// <summary>
/// Single source of truth for the channel-name vocabulary shared by
/// settings ("injectionChannels"), the inject_via= / inject_gates=
/// telemetry, and log lines. Canonical spellings are camelCase per the
/// design doc §2.3; parsing is case-insensitive. Unknown names are
/// reported to the caller (which logs a warning) and skipped; an empty or
/// fully-invalid list falls back to the hardcoded default ladder.
/// </summary>
public static class InjectionChannelNames
{
    public const string EmReplaceSelName = "emReplaceSel";
    public const string WmCharSmtoName = "wmCharSmto";
    public const string VkPacketName = "vkPacket";

    /// <summary>Hardcoded default ladder order (design doc §5 decision 1).</summary>
    public static readonly IReadOnlyList<DeliveryChannel> DefaultLadder = new[]
    {
        DeliveryChannel.EmReplaceSel,
        DeliveryChannel.WmCharSmto,
        DeliveryChannel.VkPacket,
    };

    public static string Name(DeliveryChannel channel) => channel switch
    {
        DeliveryChannel.EmReplaceSel => EmReplaceSelName,
        DeliveryChannel.WmCharSmto => WmCharSmtoName,
        _ => VkPacketName,
    };

    public static bool TryParse(string? name, out DeliveryChannel channel)
    {
        if (string.Equals(name, EmReplaceSelName, StringComparison.OrdinalIgnoreCase))
        {
            channel = DeliveryChannel.EmReplaceSel;
            return true;
        }
        if (string.Equals(name, WmCharSmtoName, StringComparison.OrdinalIgnoreCase))
        {
            channel = DeliveryChannel.WmCharSmto;
            return true;
        }
        if (string.Equals(name, VkPacketName, StringComparison.OrdinalIgnoreCase))
        {
            channel = DeliveryChannel.VkPacket;
            return true;
        }
        channel = default;
        return false;
    }

    /// <summary>
    /// Parse a settings-supplied channel-name list into a ladder order.
    /// Unknown names invoke <paramref name="onUnknown"/> and are skipped;
    /// duplicates keep the first occurrence (gates run once per rung per
    /// run); null/empty/fully-invalid input yields <see cref="DefaultLadder"/>.
    /// </summary>
    public static IReadOnlyList<DeliveryChannel> ParseLadder(
        IReadOnlyList<string>? names, Action<string>? onUnknown = null)
    {
        if (names is null || names.Count == 0) return DefaultLadder;
        var result = new List<DeliveryChannel>(names.Count);
        foreach (var name in names)
        {
            if (TryParse(name, out var channel))
            {
                if (!result.Contains(channel)) result.Add(channel);
            }
            else
            {
                onUnknown?.Invoke(name ?? "<null>");
            }
        }
        return result.Count == 0 ? DefaultLadder : result;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run **[BUILD-PLATFORM]** then **[RUN-CLASS]** with `InjectionChannelNamesTests`.
Expected: all tests PASS (`Failed: 0, Errors: 0`).

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh   # must end with: LINUX SUITE: GREEN
git add src/Winpepper.Platform/Injection/DeliveryChannel.cs \
        src/Winpepper.Platform/Injection/InjectionChannelNames.cs \
        tests/Winpepper.Platform.Tests/Injection/InjectionChannelNamesTests.cs
git commit -m "feat(injection): DeliveryChannel enum and channel-name parsing for the delivery ladder"
```

---

### Task 2: FocusedChildCapture + FocusedChildProbe (double-sample hardening logic)

**Files:**
- Create: `src/Winpepper.Platform/Injection/FocusedChildCapture.cs`
- Create: `src/Winpepper.Platform/Injection/FocusedChildProbe.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/FocusedChildProbeTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public readonly record struct FocusedChildCapture(long FocusedChildHwnd, bool Stable)`
  (invariant: `!Stable ⇒ FocusedChildHwnd == 0`);
  `internal static class FocusedChildProbe` with `internal const int SampleGapMs = 30`
  and `FocusedChildCapture Capture(long foregroundHwnd, Func<long, long> sampleFocusedChild, Action<int> sleep)`.
  Used by Tasks 3 (real sampler plugs in), 7 (TextInjector default capture), 11.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Platform.Tests/Injection/FocusedChildProbeTests.cs`:

```csharp
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class FocusedChildProbeTests
{
    [Fact]
    public void EqualNonzeroSamples_AreStable_WithThirtyMsGap()
    {
        var sleeps = new List<int>();
        var capture = FocusedChildProbe.Capture(42, _ => 7, sleeps.Add);

        capture.Stable.ShouldBeTrue();
        capture.FocusedChildHwnd.ShouldBe(7L);
        sleeps.ShouldBe(new[] { 30 }); // >= 30 ms between the two samples
    }

    [Fact]
    public void DisagreeingSamples_AreUnstable_WithZeroEffectiveHwnd()
    {
        var sample = 0;
        var capture = FocusedChildProbe.Capture(42, _ => ++sample == 1 ? 7L : 9L, _ => { });

        capture.Stable.ShouldBeFalse();
        capture.FocusedChildHwnd.ShouldBe(0L); // unstable => effective hwnd is 0
    }

    [Fact]
    public void SecondSampleZero_IsUnstable()
    {
        var sample = 0;
        var capture = FocusedChildProbe.Capture(42, _ => ++sample == 1 ? 7L : 0L, _ => { });

        capture.Stable.ShouldBeFalse();
        capture.FocusedChildHwnd.ShouldBe(0L);
    }

    [Fact]
    public void FirstSampleZero_IsUnstable_WithoutSleepingOrResampling()
    {
        // Pinned decision #9: a zero first sample already determines the
        // outcome; skip the 30 ms gap (keeps fake-hwnd unit tests and the
        // production no-focus path free of a pointless stall).
        var calls = 0;
        var sleeps = new List<int>();
        var capture = FocusedChildProbe.Capture(42, _ => { calls++; return 0; }, sleeps.Add);

        capture.Stable.ShouldBeFalse();
        capture.FocusedChildHwnd.ShouldBe(0L);
        calls.ShouldBe(1);
        sleeps.ShouldBeEmpty();
    }

    [Fact]
    public void SamplerReceives_TheForegroundHwnd()
    {
        var seen = new List<long>();
        FocusedChildProbe.Capture(42, h => { seen.Add(h); return 7; }, _ => { });
        seen.ShouldBe(new[] { 42L, 42L });
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run **[BUILD-PLATFORM]**. Expected: FAIL with CS0246 (`FocusedChildProbe`,
`FocusedChildCapture` not found).

- [ ] **Step 3: Implement**

Create `src/Winpepper.Platform/Injection/FocusedChildCapture.cs`:

```csharp
namespace Winpepper.Platform.Injection;

/// <summary>
/// Result of the send-start focused-child capture (design doc §2.2
/// hardening, motivated by the E7c wrong-target anomalies).
/// FocusedChildHwnd is the EFFECTIVE handle: 0 whenever the double-sample
/// was unstable or either sample was zero — that convention is how the
/// two-long IDeliveryStrategy.CanDeliver contract "receives the stability
/// fact". Invariant: !Stable implies FocusedChildHwnd == 0.
/// </summary>
public readonly record struct FocusedChildCapture(long FocusedChildHwnd, bool Stable);
```

Create `src/Winpepper.Platform/Injection/FocusedChildProbe.cs`:

```csharp
namespace Winpepper.Platform.Injection;

/// <summary>
/// Pure double-sample logic for the focused-child capture: sample the
/// target GUI thread's focus window twice, >= 30 ms apart; stable = both
/// samples equal and nonzero (design doc §2.2). Environment access is
/// injected so every path is unit-testable on Linux; the production
/// sampler is MessageDelivery.SampleFocusedChild.
/// </summary>
internal static class FocusedChildProbe
{
    internal const int SampleGapMs = 30;

    public static FocusedChildCapture Capture(
        long foregroundHwnd,
        Func<long, long> sampleFocusedChild,
        Action<int> sleep)
    {
        var first = sampleFocusedChild(foregroundHwnd);
        // A zero first sample already determines the verdict (unstable):
        // skip the gap and the second sample.
        if (first == 0) return new FocusedChildCapture(0, false);
        sleep(SampleGapMs);
        var second = sampleFocusedChild(foregroundHwnd);
        var stable = first == second;
        return new FocusedChildCapture(stable ? first : 0, stable);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run **[BUILD-PLATFORM]** then **[RUN-CLASS]** with `FocusedChildProbeTests`.
Expected: PASS.

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh   # LINUX SUITE: GREEN
git add src/Winpepper.Platform/Injection/FocusedChildCapture.cs \
        src/Winpepper.Platform/Injection/FocusedChildProbe.cs \
        tests/Winpepper.Platform.Tests/Injection/FocusedChildProbeTests.cs
git commit -m "feat(injection): focused-child double-sample capture hardening (spec 2.2)"
```

---

### Task 3: Win32 surface — MessageDeliveryNative + MessageDelivery wrappers

**Files:**
- Create: `src/Winpepper.Platform/Injection/MessageDeliveryNative.cs`
- Create: `src/Winpepper.Platform/Injection/MessageDelivery.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/MessageDeliveryTests.cs`

**Interfaces:**
- Consumes: `ElevationNative.GetWindowThreadProcessId(IntPtr, out uint)` (already
  exists in `src/Winpepper.Platform/Injection/ElevationNative.cs`).
- Produces: `internal static class MessageDelivery` with
  `string? ClassName(long hwnd)`, `bool EmGetSelProbe(long hwnd)`,
  `bool SendReplaceSel(long hwnd, string chunk)`,
  `bool SendCharSmto(long hwnd, ushort unit)`,
  `long SampleFocusedChild(long foregroundHwnd)`.
  All return null/false/0 off-Windows. Used by Tasks 4, 5, 7, 11.

- [ ] **Step 1: Write the failing tests** (off-Windows fallback pins — these are
what the Linux suite can prove; the real Win32 behavior is Task 11's job)

Create `tests/Winpepper.Platform.Tests/Injection/MessageDeliveryTests.cs`:

```csharp
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class MessageDeliveryTests
{
    // Off-Windows every wrapper must fail closed (null/false/0) so the
    // ladder degrades to VkPacket instead of throwing — the same
    // OperatingSystem.IsWindows() guard discipline as ElevationProbe.
    [Fact]
    public void OffWindows_AllWrappers_FailClosed()
    {
        if (OperatingSystem.IsWindows()) return; // Linux-only pin

        MessageDelivery.ClassName(42).ShouldBeNull();
        MessageDelivery.EmGetSelProbe(42).ShouldBeFalse();
        MessageDelivery.SendReplaceSel(42, "hi").ShouldBeFalse();
        MessageDelivery.SendCharSmto(42, 'h').ShouldBeFalse();
        MessageDelivery.SampleFocusedChild(42).ShouldBe(0L);
    }

    [Fact]
    public void ZeroHwnd_FailsClosed_OnAnyPlatform()
    {
        MessageDelivery.ClassName(0).ShouldBeNull();
        MessageDelivery.EmGetSelProbe(0).ShouldBeFalse();
        MessageDelivery.SendReplaceSel(0, "hi").ShouldBeFalse();
        MessageDelivery.SendCharSmto(0, 'h').ShouldBeFalse();
        MessageDelivery.SampleFocusedChild(0).ShouldBe(0L);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run **[BUILD-PLATFORM]**. Expected: FAIL with CS0246 (`MessageDelivery` not found).

- [ ] **Step 3: Implement the native surface**

Create `src/Winpepper.Platform/Injection/MessageDeliveryNative.cs`:

```csharp
using System.Runtime.InteropServices;

namespace Winpepper.Platform.Injection;

/// <summary>
/// P/Invoke surface (user32) for the message-based delivery rungs and the
/// focused-child capture (design doc §2.2): GetGUIThreadInfo for the
/// double-sample, GetClassNameW for the rung-1 gate, and SendMessageTimeoutW
/// (IntPtr and string lParam overloads) for EM_GETSEL / EM_REPLACESEL /
/// WM_CHAR sends. All calls are made only behind OperatingSystem.IsWindows()
/// runtime checks in MessageDelivery.
/// </summary>
internal static partial class MessageDeliveryNative
{
    public const uint EM_GETSEL = 0x00B0;
    public const uint EM_REPLACESEL = 0x00C2;
    public const uint WM_CHAR = 0x0102;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>Pinned SMTO timeout for both gates and sends (design doc §2.2: "SMTO, 150 ms").</summary>
    public const uint SmtoTimeoutMs = 150;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO pgui);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassNameW(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    public static partial IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
```

(Validated 2026-08-07: this exact surface — `[Out] char[]` + `StringMarshalling.Utf16`,
both `SendMessageTimeoutW` overloads, and `ref GUITHREADINFO` with nested `RECT` —
compiles clean on this host's net9.0 SDK with `-p:EnableWindowsTargeting=true`, 0
warnings; the fallback below is NOT expected to be needed. Note `LibraryImport`
requires `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` (SYSLIB1062 otherwise), which
`Winpepper.Platform.csproj` already sets — do not remove it. If the source generator
nevertheless rejects the `char[]` parameter, use
`[Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] lpClassName`
and drop `StringMarshalling` from that one attribute — behavior is identical.)

Create `src/Winpepper.Platform/Injection/MessageDelivery.cs`:

```csharp
using System.Runtime.InteropServices;

namespace Winpepper.Platform.Injection;

/// <summary>
/// Managed wrappers over MessageDeliveryNative, guarded by
/// OperatingSystem.IsWindows() so the pure routing/gating logic above them
/// is exercisable on Linux (everything fails closed off-Windows: the
/// ladder then degrades to the VkPacket floor = status quo). These are the
/// production defaults behind the per-strategy ctor seams.
/// </summary>
internal static class MessageDelivery
{
    /// <summary>Window class name of hwnd, or null when unavailable.</summary>
    public static string? ClassName(long hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return null;
        var buffer = new char[256];
        var length = MessageDeliveryNative.GetClassNameW((IntPtr)hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    /// <summary>Side-effect-free EM_GETSEL probe via SMTO; true = the target answered within 150 ms.</summary>
    public static bool EmGetSelProbe(long hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return false;
        return MessageDeliveryNative.SendMessageTimeout(
            (IntPtr)hwnd, MessageDeliveryNative.EM_GETSEL, IntPtr.Zero, IntPtr.Zero,
            MessageDeliveryNative.SMTO_ABORTIFHUNG, MessageDeliveryNative.SmtoTimeoutMs,
            out _) != IntPtr.Zero;
    }

    /// <summary>One EM_REPLACESEL (wParam=1: undoable) carrying the whole chunk string; false = refused/timed out.</summary>
    public static bool SendReplaceSel(long hwnd, string chunk)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return false;
        return MessageDeliveryNative.SendMessageTimeout(
            (IntPtr)hwnd, MessageDeliveryNative.EM_REPLACESEL, (IntPtr)1, chunk,
            MessageDeliveryNative.SMTO_ABORTIFHUNG, MessageDeliveryNative.SmtoTimeoutMs,
            out _) != IntPtr.Zero;
    }

    /// <summary>One WM_CHAR for one UTF-16 code unit (lParam=1: repeat count); false = refused/timed out.</summary>
    public static bool SendCharSmto(long hwnd, ushort unit)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return false;
        return MessageDeliveryNative.SendMessageTimeout(
            (IntPtr)hwnd, MessageDeliveryNative.WM_CHAR, (IntPtr)unit, (IntPtr)1,
            MessageDeliveryNative.SMTO_ABORTIFHUNG, MessageDeliveryNative.SmtoTimeoutMs,
            out _) != IntPtr.Zero;
    }

    /// <summary>
    /// One focused-child sample: resolve the foreground window's GUI thread
    /// (GetWindowThreadProcessId) and read GetGUIThreadInfo(...).hwndFocus.
    /// 0 when anything along the chain is unavailable.
    /// </summary>
    public static long SampleFocusedChild(long foregroundHwnd)
    {
        if (!OperatingSystem.IsWindows() || foregroundHwnd == 0) return 0;
        var threadId = ElevationNative.GetWindowThreadProcessId((IntPtr)foregroundHwnd, out _);
        if (threadId == 0) return 0;
        var info = new MessageDeliveryNative.GUITHREADINFO
        {
            cbSize = (uint)Marshal.SizeOf<MessageDeliveryNative.GUITHREADINFO>(),
        };
        if (!MessageDeliveryNative.GetGUIThreadInfo(threadId, ref info)) return 0;
        return info.hwndFocus.ToInt64();
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run **[BUILD-PLATFORM]** then **[RUN-CLASS]** with `MessageDeliveryTests`.
Expected: PASS.

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh   # LINUX SUITE: GREEN
git add src/Winpepper.Platform/Injection/MessageDeliveryNative.cs \
        src/Winpepper.Platform/Injection/MessageDelivery.cs \
        tests/Winpepper.Platform.Tests/Injection/MessageDeliveryTests.cs
git commit -m "feat(injection): SMTO/GUI-thread Win32 surface for message-based delivery rungs"
```

---

### Task 4: IDeliveryStrategy contract + EmReplaceSelStrategy (rung 1)

**Files:**
- Create: `src/Winpepper.Platform/Injection/IDeliveryStrategy.cs`
- Create: `src/Winpepper.Platform/Injection/EmReplaceSelStrategy.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/EmReplaceSelStrategyTests.cs`

**Interfaces:**
- Consumes: `DeliveryChannel` (Task 1), `MessageDelivery.ClassName/EmGetSelProbe/SendReplaceSel` (Task 3).
- Produces: `internal interface IDeliveryStrategy` (EXACTLY the pinned contract);
  `internal sealed class EmReplaceSelStrategy(ILogger log, Func<long, string?>? className = null, Func<long, bool>? emGetSelProbe = null, Func<long, string, bool>? sendReplaceSel = null)`.
  Used by Tasks 5, 6, 7, 11. Tests reach internal types via the existing
  `<InternalsVisibleTo Include="Winpepper.Platform.Tests" />` in Winpepper.Platform.csproj.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Platform.Tests/Injection/EmReplaceSelStrategyTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class EmReplaceSelStrategyTests
{
    private static EmReplaceSelStrategy NewStrategy(
        Func<long, string?> className,
        Func<long, bool>? emGetSelProbe = null,
        Func<long, string, bool>? sendReplaceSel = null)
        => new(
            NullLogger.Instance,
            className: className,
            emGetSelProbe: emGetSelProbe ?? (_ => true),
            sendReplaceSel: sendReplaceSel ?? ((_, _) => true));

    [Fact]
    public void Channel_IsEmReplaceSel()
    {
        NewStrategy(_ => "Edit").Channel.ShouldBe(DeliveryChannel.EmReplaceSel);
    }

    [Theory]
    [InlineData("Edit")]              // classic EDIT
    [InlineData("RICHEDIT50W")]       // rich edit
    [InlineData("RichEditD2DPT")]     // Win11 Notepad
    public void Gate_Passes_WhenClassContainsEdit_CaseInsensitive(string cls)
    {
        NewStrategy(_ => cls).CanDeliver(42, 7).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Chrome_RenderWidgetHostHWND")]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS")]
    [InlineData(null)]
    public void Gate_Fails_WhenClassDoesNotContainEdit(string? cls)
    {
        NewStrategy(_ => cls).CanDeliver(42, 7).ShouldBeFalse();
    }

    [Fact]
    public void Gate_Fails_WhenEmGetSelProbeFails()
    {
        NewStrategy(_ => "Edit", emGetSelProbe: _ => false).CanDeliver(42, 7).ShouldBeFalse();
    }

    [Fact]
    public void Gate_Fails_OnZeroFocusedChild_WithoutProbing()
    {
        // 0 encodes "unstable or no focused child" (pinned decision #2) —
        // the gate must fail closed without touching the target.
        var strategy = NewStrategy(
            _ => throw new InvalidOperationException("must not probe class"),
            emGetSelProbe: _ => throw new InvalidOperationException("must not probe EM_GETSEL"));
        strategy.CanDeliver(42, 0).ShouldBeFalse();
    }

    [Fact]
    public void Gate_ProbesTheFocusedChild_NotTheForeground()
    {
        var probed = new List<long>();
        var strategy = NewStrategy(
            h => { probed.Add(h); return "Edit"; },
            emGetSelProbe: h => { probed.Add(h); return true; });
        strategy.CanDeliver(42, 7).ShouldBeTrue();
        probed.ShouldBe(new[] { 7L, 7L });
    }

    [Fact]
    public void TrySendChunk_DelegatesToReplaceSel_AndReportsResult()
    {
        var sent = new List<(long Hwnd, string Chunk)>();
        var strategy = NewStrategy(_ => "Edit",
            sendReplaceSel: (h, c) => { sent.Add((h, c)); return true; });

        strategy.TrySendChunk(7, "hello wo").ShouldBeTrue();
        sent.ShouldBe(new[] { (7L, "hello wo") });
    }

    [Fact]
    public void TrySendChunk_False_OnRefusedSend()
    {
        NewStrategy(_ => "Edit", sendReplaceSel: (_, _) => false)
            .TrySendChunk(7, "hello wo").ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run **[BUILD-PLATFORM]**. Expected: FAIL with CS0246 (`EmReplaceSelStrategy` not found).

- [ ] **Step 3: Implement**

Create `src/Winpepper.Platform/Injection/IDeliveryStrategy.cs` — the contract is
pinned by the design doc §2.1; copy it EXACTLY:

```csharp
namespace Winpepper.Platform.Injection;

/// <summary>
/// One rung of the injection delivery ladder (design doc 2026-08-06 §2.1).
/// Strategies deliver chunks only — chunking, the guarded run loop, halts,
/// pacing, elevation handling, and the failure pill are shared and
/// unchanged, and are NOT part of this contract.
/// </summary>
internal interface IDeliveryStrategy
{
    DeliveryChannel Channel { get; }

    /// Capability gate: runs ONCE at send start, before any text is sent.
    /// Must be side-effect-free on the target document (probing messages only).
    /// focusedChildHwnd is the EFFECTIVE capture result: 0 when the
    /// double-sample was unstable or empty (FocusedChildCapture invariant).
    bool CanDeliver(long foregroundHwnd, long focusedChildHwnd);

    /// Deliver one chunk. False = refused/failed -> the run STOPS and maps to
    /// the existing SendFailed flow (pill shows transcript with click-to-copy).
    /// Never throws for target-side failure.
    bool TrySendChunk(long targetHwnd, string chunk);
}
```

Create `src/Winpepper.Platform/Injection/EmReplaceSelStrategy.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Winpepper.Platform.Injection;

/// <summary>
/// Rung 1 (design doc §2.2, validated E6+E9a): one EM_REPLACESEL per chunk
/// via SendMessageTimeout (150 ms). Gate: focused-child class contains
/// "edit" (case-insensitive) AND a side-effect-free SMTO EM_GETSEL probe
/// answers AND the capture was stable (encoded as focusedChildHwnd != 0).
/// Fastest rung (~2x VK_PACKET) and immune to the cold-Notepad async-drop
/// class because delivery is synchronous.
/// </summary>
internal sealed class EmReplaceSelStrategy : IDeliveryStrategy
{
    private readonly ILogger _log;
    private readonly Func<long, string?> _className;
    private readonly Func<long, bool> _emGetSelProbe;
    private readonly Func<long, string, bool> _sendReplaceSel;

    public EmReplaceSelStrategy(
        ILogger log,
        Func<long, string?>? className = null,
        Func<long, bool>? emGetSelProbe = null,
        Func<long, string, bool>? sendReplaceSel = null)
    {
        _log = log;
        _className = className ?? MessageDelivery.ClassName;
        _emGetSelProbe = emGetSelProbe ?? MessageDelivery.EmGetSelProbe;
        _sendReplaceSel = sendReplaceSel ?? MessageDelivery.SendReplaceSel;
    }

    public DeliveryChannel Channel => DeliveryChannel.EmReplaceSel;

    public bool CanDeliver(long foregroundHwnd, long focusedChildHwnd)
    {
        if (focusedChildHwnd == 0) return false; // unstable or no focused child
        var cls = _className(focusedChildHwnd);
        if (cls is null || !cls.Contains("edit", StringComparison.OrdinalIgnoreCase))
            return false;
        return _emGetSelProbe(focusedChildHwnd);
    }

    public bool TrySendChunk(long targetHwnd, string chunk)
    {
        if (_sendReplaceSel(targetHwnd, chunk)) return true;
        _log.LogWarning(
            "EM_REPLACESEL send refused or timed out (hwnd 0x{Hwnd:X}); stopping the run",
            targetHwnd);
        return false;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run **[BUILD-PLATFORM]** then **[RUN-CLASS]** with `EmReplaceSelStrategyTests`.
Expected: PASS.

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh   # LINUX SUITE: GREEN
git add src/Winpepper.Platform/Injection/IDeliveryStrategy.cs \
        src/Winpepper.Platform/Injection/EmReplaceSelStrategy.cs \
        tests/Winpepper.Platform.Tests/Injection/EmReplaceSelStrategyTests.cs
git commit -m "feat(injection): IDeliveryStrategy contract and EmReplaceSel rung"
```

---

### Task 5: WmCharSmtoStrategy (rung 2) + VkPacketStrategy (rung 3)

**Files:**
- Create: `src/Winpepper.Platform/Injection/WmCharSmtoStrategy.cs`
- Create: `src/Winpepper.Platform/Injection/VkPacketStrategy.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/WmCharSmtoStrategyTests.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/VkPacketStrategyTests.cs`

**Interfaces:**
- Consumes: `IDeliveryStrategy`, `DeliveryChannel`, `MessageDelivery.SendCharSmto`.
- Produces:
  `internal sealed class WmCharSmtoStrategy(ILogger log, Func<long, ushort, bool>? sendChar = null)`;
  `internal sealed class VkPacketStrategy(Func<string, bool> sendChunk)` —
  `sendChunk` is REQUIRED and is wired by TextInjector to its existing
  `_sendChunk` delegate (default `SendChunkViaSendInput`), which is what makes
  rung 3 byte-identical to today's behavior. Used by Tasks 6, 7, 11.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Platform.Tests/Injection/WmCharSmtoStrategyTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class WmCharSmtoStrategyTests
{
    [Fact]
    public void Channel_IsWmCharSmto()
    {
        new WmCharSmtoStrategy(NullLogger.Instance, (_, _) => true)
            .Channel.ShouldBe(DeliveryChannel.WmCharSmto);
    }

    [Fact]
    public void Gate_Passes_WhenFocusedChildObservable_AndStable()
    {
        // Gate is exactly "focused child observable + stable": stability is
        // encoded upstream as a nonzero effective hwnd (pinned decision #2).
        new WmCharSmtoStrategy(NullLogger.Instance, (_, _) => true)
            .CanDeliver(42, 7).ShouldBeTrue();
    }

    [Fact]
    public void Gate_Fails_OnZeroFocusedChild()
    {
        new WmCharSmtoStrategy(NullLogger.Instance, (_, _) => true)
            .CanDeliver(42, 0).ShouldBeFalse();
    }

    [Fact]
    public void TrySendChunk_SendsOneMessagePerUtf16Unit_InOrder()
    {
        var sent = new List<(long Hwnd, ushort Unit)>();
        var strategy = new WmCharSmtoStrategy(
            NullLogger.Instance, (h, u) => { sent.Add((h, u)); return true; });

        // "a" + G-clef (U+1D11E, one surrogate pair = two units) + "b"
        var chunk = "a\uD834\uDD1Eb";
        strategy.TrySendChunk(7, chunk).ShouldBeTrue();

        sent.Select(s => s.Unit).ShouldBe(new ushort[] { 'a', 0xD834, 0xDD1E, 'b' });
        sent.All(s => s.Hwnd == 7).ShouldBeTrue();
    }

    [Fact]
    public void TrySendChunk_StopsAtFirstRefusedUnit_AndReturnsFalse()
    {
        var sentCount = 0;
        var strategy = new WmCharSmtoStrategy(
            NullLogger.Instance, (_, _) => ++sentCount < 3); // 3rd unit refused

        strategy.TrySendChunk(7, "abcdefgh").ShouldBeFalse();
        sentCount.ShouldBe(3); // units 4..8 never attempted
    }
}
```

Create `tests/Winpepper.Platform.Tests/Injection/VkPacketStrategyTests.cs`:

```csharp
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class VkPacketStrategyTests
{
    [Fact]
    public void Channel_IsVkPacket()
    {
        new VkPacketStrategy(_ => true).Channel.ShouldBe(DeliveryChannel.VkPacket);
    }

    [Fact]
    public void Gate_AlwaysPasses_EvenWithZeroFocusedChild()
    {
        var strategy = new VkPacketStrategy(_ => true);
        strategy.CanDeliver(42, 0).ShouldBeTrue();
        strategy.CanDeliver(0, 0).ShouldBeTrue();
    }

    [Fact]
    public void TrySendChunk_DelegatesToWrappedSend_IgnoringTargetHwnd()
    {
        var sent = new List<string>();
        var strategy = new VkPacketStrategy(c => { sent.Add(c); return true; });

        strategy.TrySendChunk(0, "hello wo").ShouldBeTrue();   // hwnd irrelevant
        strategy.TrySendChunk(999, "rld").ShouldBeTrue();
        sent.ShouldBe(new[] { "hello wo", "rld" });
    }

    [Fact]
    public void TrySendChunk_PropagatesFailure()
    {
        new VkPacketStrategy(_ => false).TrySendChunk(7, "x").ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run **[BUILD-PLATFORM]**. Expected: FAIL with CS0246.

- [ ] **Step 3: Implement**

Create `src/Winpepper.Platform/Injection/WmCharSmtoStrategy.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Winpepper.Platform.Injection;

/// <summary>
/// Rung 2 (design doc §2.2, validated E7+E9e): one WM_CHAR per UTF-16 code
/// unit via SendMessageTimeout (SMTO_ABORTIFHUNG, 150 ms). Synchronous
/// delivery survives the cold-Notepad class and is phantom-Ctrl-immune (no
/// translation step). Gate: focused child observable + stable (encoded as
/// focusedChildHwnd != 0). Cost honestly stated in the doc: ~0.8 s per 134
/// units on targets that reach this rung.
/// </summary>
internal sealed class WmCharSmtoStrategy : IDeliveryStrategy
{
    private readonly ILogger _log;
    private readonly Func<long, ushort, bool> _sendChar;

    public WmCharSmtoStrategy(ILogger log, Func<long, ushort, bool>? sendChar = null)
    {
        _log = log;
        _sendChar = sendChar ?? MessageDelivery.SendCharSmto;
    }

    public DeliveryChannel Channel => DeliveryChannel.WmCharSmto;

    public bool CanDeliver(long foregroundHwnd, long focusedChildHwnd)
        => focusedChildHwnd != 0;

    public bool TrySendChunk(long targetHwnd, string chunk)
    {
        for (var i = 0; i < chunk.Length; i++)
        {
            if (!_sendChar(targetHwnd, chunk[i]))
            {
                _log.LogWarning(
                    "WM_CHAR send refused or timed out at unit {Index}/{Count} (hwnd 0x{Hwnd:X}); stopping the run",
                    i, chunk.Length, targetHwnd);
                return false;
            }
        }
        return true;
    }
}
```

Create `src/Winpepper.Platform/Injection/VkPacketStrategy.cs`:

```csharp
namespace Winpepper.Platform.Injection;

/// <summary>
/// Rung 3, the status-quo floor (design doc §2.2): EXACTLY today's
/// SendInput KEYEVENTF_UNICODE chunk send. This class only WRAPS the
/// existing send delegate (TextInjector wires its _sendChunk — default
/// SendChunkViaSendInput — in), so behavior stays byte-identical and the
/// sendChunk ctor seam keeps working for every existing test. Gate: always
/// true. SendInput is focus-routed by the OS, so targetHwnd is unused.
/// </summary>
internal sealed class VkPacketStrategy : IDeliveryStrategy
{
    private readonly Func<string, bool> _sendChunk;

    public VkPacketStrategy(Func<string, bool> sendChunk) => _sendChunk = sendChunk;

    public DeliveryChannel Channel => DeliveryChannel.VkPacket;

    public bool CanDeliver(long foregroundHwnd, long focusedChildHwnd) => true;

    public bool TrySendChunk(long targetHwnd, string chunk) => _sendChunk(chunk);
}
```

- [ ] **Step 4: Run to verify pass**

Run **[BUILD-PLATFORM]** then **[RUN-CLASS]** with `WmCharSmtoStrategyTests` and
again with `VkPacketStrategyTests`. Expected: PASS.

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh   # LINUX SUITE: GREEN
git add src/Winpepper.Platform/Injection/WmCharSmtoStrategy.cs \
        src/Winpepper.Platform/Injection/VkPacketStrategy.cs \
        tests/Winpepper.Platform.Tests/Injection/WmCharSmtoStrategyTests.cs \
        tests/Winpepper.Platform.Tests/Injection/VkPacketStrategyTests.cs
git commit -m "feat(injection): WmCharSmto rung and VkPacket floor wrapping the existing send"
```

---

### Task 6: DeliveryLadder router (first-passing-gate, gates record, floor fallback)

**Files:**
- Create: `src/Winpepper.Platform/Injection/DeliveryLadder.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/DeliveryLadderTests.cs`

**Interfaces:**
- Consumes: `IDeliveryStrategy`, `DeliveryChannel`, `InjectionChannelNames.Name`,
  `FocusedChildCapture`.
- Produces:
  `internal readonly record struct DeliverySelection(IDeliveryStrategy Strategy, string GatesSummary)`;
  `internal static class DeliveryLadder` with
  `const string ReasonNoEm = "no-em"`, `const string ReasonFocusUnstable = "focus-unstable"`, and
  `DeliverySelection Select(IReadOnlyList<DeliveryChannel> order, IReadOnlyList<IDeliveryStrategy> strategies, long foregroundHwnd, FocusedChildCapture capture)`.
  Used by Task 7 (TextInjector).

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Platform.Tests/Injection/DeliveryLadderTests.cs`:

```csharp
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class DeliveryLadderTests
{
    private sealed class FakeStrategy : IDeliveryStrategy
    {
        private readonly bool _canDeliver;
        public int CanDeliverCalls;
        public readonly List<long> GatedHwnds = new();

        public FakeStrategy(DeliveryChannel channel, bool canDeliver)
        {
            Channel = channel;
            _canDeliver = canDeliver;
        }

        public DeliveryChannel Channel { get; }

        public bool CanDeliver(long foregroundHwnd, long focusedChildHwnd)
        {
            CanDeliverCalls++;
            GatedHwnds.Add(focusedChildHwnd);
            return _canDeliver;
        }

        public bool TrySendChunk(long targetHwnd, string chunk) => true;
    }

    private static readonly IReadOnlyList<DeliveryChannel> FullOrder =
        InjectionChannelNames.DefaultLadder;

    [Fact]
    public void FirstPassingGate_Wins_AndLaterGatesAreNotEvaluated()
    {
        var em = new FakeStrategy(DeliveryChannel.EmReplaceSel, canDeliver: true);
        var wm = new FakeStrategy(DeliveryChannel.WmCharSmto, canDeliver: true);
        var vk = new FakeStrategy(DeliveryChannel.VkPacket, canDeliver: true);

        var selection = DeliveryLadder.Select(
            FullOrder, new IDeliveryStrategy[] { em, wm, vk }, 42, new FocusedChildCapture(7, true));

        selection.Strategy.ShouldBeSameAs(em);
        selection.GatesSummary.ShouldBe(string.Empty); // empty when the first rung delivered
        em.CanDeliverCalls.ShouldBe(1);
        wm.CanDeliverCalls.ShouldBe(0);
        vk.CanDeliverCalls.ShouldBe(0);
    }

    [Fact]
    public void GatesAreEvaluated_InConfiguredOrder_OnceEach()
    {
        var em = new FakeStrategy(DeliveryChannel.EmReplaceSel, canDeliver: false);
        var wm = new FakeStrategy(DeliveryChannel.WmCharSmto, canDeliver: false);
        var vk = new FakeStrategy(DeliveryChannel.VkPacket, canDeliver: true);

        var selection = DeliveryLadder.Select(
            FullOrder, new IDeliveryStrategy[] { vk, wm, em }, // registration order irrelevant
            42, new FocusedChildCapture(7, true));

        selection.Strategy.ShouldBeSameAs(vk);
        em.CanDeliverCalls.ShouldBe(1);
        wm.CanDeliverCalls.ShouldBe(1);
        vk.CanDeliverCalls.ShouldBe(1);
    }

    [Fact]
    public void ConfiguredOrder_IsHonored()
    {
        var em = new FakeStrategy(DeliveryChannel.EmReplaceSel, canDeliver: true);
        var vk = new FakeStrategy(DeliveryChannel.VkPacket, canDeliver: true);

        var selection = DeliveryLadder.Select(
            new[] { DeliveryChannel.VkPacket, DeliveryChannel.EmReplaceSel },
            new IDeliveryStrategy[] { em, vk }, 42, new FocusedChildCapture(7, true));

        selection.Strategy.ShouldBeSameAs(vk); // vkPacket listed first wins
        em.CanDeliverCalls.ShouldBe(0);
    }

    [Fact]
    public void GatesSummary_RecordsGatedRungs_WithStableFocusReason()
    {
        var em = new FakeStrategy(DeliveryChannel.EmReplaceSel, canDeliver: false);
        var wm = new FakeStrategy(DeliveryChannel.WmCharSmto, canDeliver: true);

        var selection = DeliveryLadder.Select(
            FullOrder, new IDeliveryStrategy[] { em, wm }, 42, new FocusedChildCapture(7, true));

        selection.Strategy.ShouldBeSameAs(wm);
        selection.GatesSummary.ShouldBe("emReplaceSel:no-em");
    }

    [Fact]
    public void GatesSummary_UsesFocusUnstableReason_WhenCaptureUnstable()
    {
        var em = new FakeStrategy(DeliveryChannel.EmReplaceSel, canDeliver: false);
        var wm = new FakeStrategy(DeliveryChannel.WmCharSmto, canDeliver: false);
        var vk = new FakeStrategy(DeliveryChannel.VkPacket, canDeliver: true);

        var selection = DeliveryLadder.Select(
            FullOrder, new IDeliveryStrategy[] { em, wm, vk },
            42, new FocusedChildCapture(0, false));

        selection.Strategy.ShouldBeSameAs(vk);
        selection.GatesSummary.ShouldBe("emReplaceSel:focus-unstable,wmCharSmto:focus-unstable");
    }

    [Fact]
    public void Gates_ReceiveTheEffectiveFocusedChildHwnd()
    {
        var em = new FakeStrategy(DeliveryChannel.EmReplaceSel, canDeliver: true);
        DeliveryLadder.Select(FullOrder, new IDeliveryStrategy[] { em }, 42,
            new FocusedChildCapture(7, true));
        em.GatedHwnds.ShouldBe(new[] { 7L });
    }

    [Fact]
    public void ExhaustedLadder_FallsBackToVkPacketFloor_AndKeepsGatesRecord()
    {
        // Settings removed vkPacket and everything else gated out: degrade
        // to the status-quo floor rather than dropping the run (pinned
        // decision #4; design doc §3 "rungs degrade to the VK_PACKET floor").
        var em = new FakeStrategy(DeliveryChannel.EmReplaceSel, canDeliver: false);
        var vk = new FakeStrategy(DeliveryChannel.VkPacket, canDeliver: true);

        var selection = DeliveryLadder.Select(
            new[] { DeliveryChannel.EmReplaceSel },
            new IDeliveryStrategy[] { em, vk }, 42, new FocusedChildCapture(7, true));

        selection.Strategy.ShouldBeSameAs(vk);
        selection.GatesSummary.ShouldBe("emReplaceSel:no-em");
    }

    [Fact]
    public void OrderNamingAnUnregisteredChannel_IsSkipped()
    {
        var vk = new FakeStrategy(DeliveryChannel.VkPacket, canDeliver: true);
        var selection = DeliveryLadder.Select(
            new[] { DeliveryChannel.EmReplaceSel, DeliveryChannel.VkPacket },
            new IDeliveryStrategy[] { vk }, 42, new FocusedChildCapture(7, true));
        selection.Strategy.ShouldBeSameAs(vk);
        selection.GatesSummary.ShouldBe(string.Empty); // absent rung is not a gate-out
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run **[BUILD-PLATFORM]**. Expected: FAIL with CS0246 (`DeliveryLadder`).

- [ ] **Step 3: Implement**

Create `src/Winpepper.Platform/Injection/DeliveryLadder.cs`:

```csharp
using System.Text;

namespace Winpepper.Platform.Injection;

/// <summary>The winning strategy for one run plus the gates record
/// ("&lt;rung&gt;:&lt;reason&gt;" comma-list; empty when the first rung
/// delivered) — design doc §2.4 provenance, no readback, no verdicts.</summary>
internal readonly record struct DeliverySelection(IDeliveryStrategy Strategy, string GatesSummary);

/// <summary>
/// The ladder walk (design doc §2.3): in configured order, the FIRST rung
/// whose gate passes delivers the WHOLE run. Gates run once, before any
/// text is sent. No scoring, no heuristics, no app lists, no mid-run
/// re-routing. Because the pinned IDeliveryStrategy gate returns only
/// bool, the gate-out reason vocabulary lives HERE: "focus-unstable" when
/// the capture was unstable/zero, else "no-em" (the only stable-focus
/// gate-out is rung 1's class/EM_GETSEL predicate).
/// </summary>
internal static class DeliveryLadder
{
    public const string ReasonNoEm = "no-em";
    public const string ReasonFocusUnstable = "focus-unstable";

    public static DeliverySelection Select(
        IReadOnlyList<DeliveryChannel> order,
        IReadOnlyList<IDeliveryStrategy> strategies,
        long foregroundHwnd,
        FocusedChildCapture capture)
    {
        var gates = new StringBuilder();
        foreach (var channel in order)
        {
            var strategy = Find(strategies, channel);
            if (strategy is null) continue; // channel configured but not registered
            if (strategy.CanDeliver(foregroundHwnd, capture.FocusedChildHwnd))
                return new DeliverySelection(strategy, gates.ToString());
            if (gates.Length > 0) gates.Append(',');
            gates.Append(InjectionChannelNames.Name(channel))
                 .Append(':')
                 .Append(capture.Stable ? ReasonNoEm : ReasonFocusUnstable);
        }

        // The configured ladder exhausted without a passing gate — only
        // possible when settings removed vkPacket (its gate is always
        // true). Degrade to the status-quo floor rather than silently
        // dropping the run (design doc §3: "rungs degrade to the VK_PACKET
        // floor = status quo").
        var floor = Find(strategies, DeliveryChannel.VkPacket)
            ?? throw new InvalidOperationException(
                "Delivery ladder exhausted and no VkPacket floor strategy is registered");
        return new DeliverySelection(floor, gates.ToString());
    }

    private static IDeliveryStrategy? Find(
        IReadOnlyList<IDeliveryStrategy> strategies, DeliveryChannel channel)
    {
        for (var i = 0; i < strategies.Count; i++)
        {
            if (strategies[i].Channel == channel) return strategies[i];
        }
        return null;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run **[BUILD-PLATFORM]** then **[RUN-CLASS]** with `DeliveryLadderTests`.
Expected: PASS.

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh   # LINUX SUITE: GREEN
git add src/Winpepper.Platform/Injection/DeliveryLadder.cs \
        tests/Winpepper.Platform.Tests/Injection/DeliveryLadderTests.cs
git commit -m "feat(injection): delivery ladder router with gates record and VkPacket floor"
```

---

### Task 7: InjectionRunReport Via/GatesSummary + TextInjector routing integration

This is the pivotal task: after it, injection routes through the ladder end-to-end
in production code, with `GuardedInjectionRun.cs` untouched.

**Files:**
- Modify: `src/Winpepper.Platform/Injection/InjectionRunReport.cs`
- Modify: `src/Winpepper.Platform/Injection/TextInjector.cs`
- Create: `tests/Winpepper.Platform.Tests/Injection/TextInjectorLadderTests.cs`
- Modify (mechanical only): `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–6.
- Produces:
  `public readonly record struct InjectionRunReport(InjectionRunOutcome Outcome, int ChunksTotal, int ChunksSent, int PacingWaitMs, DeliveryChannel Via = DeliveryChannel.VkPacket, string? GatesSummary = null)`;
  `TextInjector` public ctor gains optional
  `Func<IReadOnlyList<DeliveryChannel>>? channelOrder = null` and
  `Func<long, FocusedChildCapture>? focusedChildCapture = null`, plus an
  `internal` ctor overload that additionally takes
  `IReadOnlyList<IDeliveryStrategy>? strategies` (the internal interface cannot
  appear on a public ctor). Used by Tasks 9-tests, 10, 11.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Platform.Tests/Injection/TextInjectorLadderTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class TextInjectorLadderTests
{
    private sealed class RecordingStrategy : IDeliveryStrategy
    {
        private readonly bool _canDeliver;
        private readonly Func<string, bool> _send;
        public int CanDeliverCalls;
        public readonly List<(long Hwnd, string Chunk)> Sent = new();
        public int SendCallsAtFirstGate = -1;

        public RecordingStrategy(DeliveryChannel channel, bool canDeliver, Func<string, bool>? send = null)
        {
            Channel = channel;
            _canDeliver = canDeliver;
            _send = send ?? (_ => true);
        }

        public DeliveryChannel Channel { get; }

        public bool CanDeliver(long foregroundHwnd, long focusedChildHwnd)
        {
            if (SendCallsAtFirstGate < 0) SendCallsAtFirstGate = Sent.Count;
            CanDeliverCalls++;
            return _canDeliver;
        }

        public bool TrySendChunk(long targetHwnd, string chunk)
        {
            Sent.Add((targetHwnd, chunk));
            return _send(chunk);
        }
    }

    private static TextInjector NewInjector(
        IReadOnlyList<IDeliveryStrategy> strategies,
        FocusedChildCapture capture,
        IReadOnlyList<DeliveryChannel>? order = null,
        Func<string, bool>? sendChunk = null)
        => new(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: sendChunk,
            sleep: _ => { },
            foregroundElevation: null,
            monotonicMs: null,
            channelOrder: () => order ?? InjectionChannelNames.DefaultLadder,
            focusedChildCapture: _ => capture,
            strategies: strategies);

    [Fact]
    public void WholeRun_DeliveredByFirstPassingRung_ToTheFixedTarget()
    {
        var em = new RecordingStrategy(DeliveryChannel.EmReplaceSel, canDeliver: true);
        var wm = new RecordingStrategy(DeliveryChannel.WmCharSmto, canDeliver: true);
        var vk = new RecordingStrategy(DeliveryChannel.VkPacket, canDeliver: true);
        var injector = NewInjector(new IDeliveryStrategy[] { em, wm, vk },
            new FocusedChildCapture(7, true));
        var text = new string('a', 80); // 10 chunks of 8

        var report = injector.TryInjectGuardedDetailed(text);

        report.Outcome.ShouldBe(InjectionRunOutcome.Completed);
        report.Via.ShouldBe(DeliveryChannel.EmReplaceSel);
        report.GatesSummary.ShouldBeNullOrEmpty();
        em.Sent.Count.ShouldBe(10);
        string.Concat(em.Sent.Select(s => s.Chunk)).ShouldBe(text);
        em.Sent.All(s => s.Hwnd == 7).ShouldBeTrue(); // SAME hwnd for every chunk
        wm.Sent.ShouldBeEmpty();
        vk.Sent.ShouldBeEmpty();
    }

    [Fact]
    public void Gates_RunOnce_BeforeAnyTextIsSent()
    {
        var em = new RecordingStrategy(DeliveryChannel.EmReplaceSel, canDeliver: false);
        var wm = new RecordingStrategy(DeliveryChannel.WmCharSmto, canDeliver: true);
        var injector = NewInjector(new IDeliveryStrategy[] { em, wm },
            new FocusedChildCapture(7, true));

        injector.TryInjectGuardedDetailed(new string('a', 80))
            .Outcome.ShouldBe(InjectionRunOutcome.Completed);

        em.CanDeliverCalls.ShouldBe(1);
        wm.CanDeliverCalls.ShouldBe(1);
        wm.SendCallsAtFirstGate.ShouldBe(0); // gate evaluated before any chunk went out
    }

    [Fact]
    public void MidRunSendFailure_StopsTheRun_NoReroute_MapsToSendFailed()
    {
        var sent = 0;
        var em = new RecordingStrategy(DeliveryChannel.EmReplaceSel, canDeliver: true,
            send: _ => ++sent < 3); // 3rd chunk refused
        var vk = new RecordingStrategy(DeliveryChannel.VkPacket, canDeliver: true);
        var injector = NewInjector(new IDeliveryStrategy[] { em, vk },
            new FocusedChildCapture(7, true));

        var report = injector.TryInjectGuardedDetailed(new string('a', 80));

        report.Outcome.ShouldBe(InjectionRunOutcome.SendFailed); // existing pill flow
        report.Via.ShouldBe(DeliveryChannel.EmReplaceSel);
        report.ChunksSent.ShouldBe(2);   // strict prefix; remaining chunks unsent
        report.ChunksTotal.ShouldBe(10);
        em.Sent.Count.ShouldBe(3);       // the refused attempt was the last touch
        vk.Sent.ShouldBeEmpty();         // NO re-route to another rung mid-text
        vk.CanDeliverCalls.ShouldBe(0);
    }

    [Fact]
    public void UnstableCapture_GatesOutRungs1And2_AndRecordsGates()
    {
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: _ => true,
            sleep: _ => { },
            focusedChildCapture: _ => new FocusedChildCapture(0, false));
        // Default strategies: real EmReplaceSel/WmCharSmto gate out on the
        // zero effective hwnd; the default VkPacket wraps the sendChunk seam.
        var report = injector.TryInjectGuardedDetailed("hello world");

        report.Outcome.ShouldBe(InjectionRunOutcome.Completed);
        report.Via.ShouldBe(DeliveryChannel.VkPacket);
        report.GatesSummary.ShouldBe("emReplaceSel:focus-unstable,wmCharSmto:focus-unstable");
    }

    [Fact]
    public void DefaultCapture_OffWindows_RoutesToVkPacket_PreservingStatusQuo()
    {
        if (OperatingSystem.IsWindows()) return; // Linux pin of the fallback path
        var sent = new List<string>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: _ => { }); // default capture + default strategies + default order

        var report = injector.TryInjectGuardedDetailed(new string('a', 16));

        report.Outcome.ShouldBe(InjectionRunOutcome.Completed);
        report.Via.ShouldBe(DeliveryChannel.VkPacket);
        string.Concat(sent).ShouldBe(new string('a', 16));
    }

    [Fact]
    public void SettingsOrder_IsHonored_PerRun()
    {
        var em = new RecordingStrategy(DeliveryChannel.EmReplaceSel, canDeliver: true);
        var vk = new RecordingStrategy(DeliveryChannel.VkPacket, canDeliver: true);
        var injector = NewInjector(new IDeliveryStrategy[] { em, vk },
            new FocusedChildCapture(7, true),
            order: new[] { DeliveryChannel.VkPacket, DeliveryChannel.EmReplaceSel });

        injector.TryInjectGuardedDetailed("hi").Via.ShouldBe(DeliveryChannel.VkPacket);
        em.CanDeliverCalls.ShouldBe(0);
    }

    [Fact]
    public void EarlyParks_KeepDefaultViaAndNoGates()
    {
        // NoForeground park happens before routing: Via must read as the
        // default (VkPacket) and no gates record exists.
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 0,
            sendChunk: _ => true,
            sleep: _ => { });

        var report = injector.TryInjectGuardedDetailed("hello");

        report.Outcome.ShouldBe(InjectionRunOutcome.NoForeground);
        report.Via.ShouldBe(DeliveryChannel.VkPacket);
        report.GatesSummary.ShouldBeNullOrEmpty();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run **[BUILD-PLATFORM]**. Expected: FAIL — `Via`/`GatesSummary` and the new ctor
parameters don't exist yet.

- [ ] **Step 3: Extend InjectionRunReport**

In `src/Winpepper.Platform/Injection/InjectionRunReport.cs`, replace the record
declaration (keep the file's existing `///` summary, extending it with the two
sentences shown):

```csharp
/// <summary>
/// Detailed outcome of one guarded injection run, for the per-dictation
/// timing summary. PacingWaitMs is the NOMINAL total of the inter-chunk
/// pause periods requested (sum of PeriodMsForChunk over invoked pauses);
/// the DeadlinePacer nets out send time at run time, and wall time is
/// measured by the caller's stopwatch. ChunksSent &lt; ChunksTotal on an
/// Interrupted/SendFailed run. ChunksTotal is 0 when the run never
/// reached chunking (NoForeground / BlockedElevated / mouse-held park).
/// Via is the delivery channel that carried (or would have carried) the
/// run; it defaults to VkPacket — including for default(InjectionRunReport),
/// which is why DeliveryChannel.VkPacket is 0. GatesSummary is the
/// "&lt;rung&gt;:&lt;reason&gt;" comma-list of rungs that gated out
/// (design doc 2026-08-06 §2.4); null/empty when the first rung delivered
/// or the run parked before routing.
/// </summary>
public readonly record struct InjectionRunReport(
    InjectionRunOutcome Outcome,
    int ChunksTotal,
    int ChunksSent,
    int PacingWaitMs,
    DeliveryChannel Via = DeliveryChannel.VkPacket,
    string? GatesSummary = null);
```

Do NOT touch the early-return construction sites in TextInjector
(`new InjectionRunReport(InjectionRunOutcome.NoForeground, 0, 0, 0)` etc.) — the
new parameters default correctly.

- [ ] **Step 4: Integrate routing into TextInjector**

All edits in `src/Winpepper.Platform/Injection/TextInjector.cs`:

**(a)** Add three fields after the existing `private readonly Func<double> _monotonicMs;`:

```csharp
    private readonly Func<IReadOnlyList<DeliveryChannel>> _channelOrder;
    private readonly Func<long, FocusedChildCapture> _focusedChildCapture;
    private readonly IReadOnlyList<IDeliveryStrategy> _strategies;
```

**(b)** Replace the existing constructor with a public/internal pair (the internal
overload exists because `IDeliveryStrategy` is internal and cannot appear on a
public ctor; tests reach it via InternalsVisibleTo):

```csharp
    public TextInjector(
        ILogger<TextInjector> log,
        Func<int, bool>? isKeyDown = null,
        Func<long>? foregroundHwnd = null,
        Func<string, bool>? sendChunk = null,
        Action<int>? sleep = null,
        Func<long, ForegroundElevation>? foregroundElevation = null,
        Func<double>? monotonicMs = null,
        Func<IReadOnlyList<DeliveryChannel>>? channelOrder = null,
        Func<long, FocusedChildCapture>? focusedChildCapture = null)
        : this(log, isKeyDown, foregroundHwnd, sendChunk, sleep, foregroundElevation,
               monotonicMs, channelOrder, focusedChildCapture, strategies: null)
    {
    }

    /// <summary>Test seam: IDeliveryStrategy is internal, so custom strategy
    /// sets enter through this internal overload (InternalsVisibleTo).</summary>
    internal TextInjector(
        ILogger<TextInjector> log,
        Func<int, bool>? isKeyDown,
        Func<long>? foregroundHwnd,
        Func<string, bool>? sendChunk,
        Action<int>? sleep,
        Func<long, ForegroundElevation>? foregroundElevation,
        Func<double>? monotonicMs,
        Func<IReadOnlyList<DeliveryChannel>>? channelOrder,
        Func<long, FocusedChildCapture>? focusedChildCapture,
        IReadOnlyList<IDeliveryStrategy>? strategies)
    {
        _log = log;
        _isKeyDown = isKeyDown ?? DefaultKeyProbe;
        _foregroundHwnd = foregroundHwnd ?? DefaultForegroundProbe;
        _sendChunk = sendChunk ?? SendChunkViaSendInput;
        _sleep = sleep ?? PacingWaiter.Wait;
        _foregroundElevation = foregroundElevation ?? ElevationProbe.Probe;
        _monotonicMs = monotonicMs ?? DefaultMonotonicMs;
        _channelOrder = channelOrder ?? (() => InjectionChannelNames.DefaultLadder);
        _focusedChildCapture = focusedChildCapture ?? DefaultFocusedChildCapture;
        // VkPacket wraps _sendChunk: rung 3 stays byte-identical to today's
        // send, and the sendChunk ctor seam keeps meaning "the VK_PACKET
        // send" for every existing test.
        _strategies = strategies ?? new IDeliveryStrategy[]
        {
            new EmReplaceSelStrategy(log),
            new WmCharSmtoStrategy(log),
            new VkPacketStrategy(_sendChunk),
        };
    }
```

**(c)** Add the default capture next to the other `Default*` private helpers:

```csharp
    /// <summary>Production focused-child capture (design doc §2.2): double-
    /// sample GetGUIThreadInfo(...).hwndFocus for the foreground window's
    /// GUI thread, >= 30 ms apart, via the injected sleep. Off-Windows the
    /// capture is unavailable => unstable => the ladder degrades to the
    /// VkPacket floor (status quo).</summary>
    private FocusedChildCapture DefaultFocusedChildCapture(long foregroundHwnd)
    {
        if (!OperatingSystem.IsWindows()) return new FocusedChildCapture(0, false);
        return FocusedChildProbe.Capture(foregroundHwnd, MessageDelivery.SampleFocusedChild, _sleep);
    }
```

**(d)** In `TryInjectGuardedDetailed`, insert routing between the mouse-wait block
and `var chunks = InjectionChunker.Split(text, ChunkCodeUnits);`:

```csharp
        // Delivery routing (design doc §2.2-§2.3): capture the focused child
        // (double-sample) and walk the ladder ONCE, before any text is sent.
        // Runs after the elevation check and the modifier/mouse preludes so
        // the capture is as close to the first send as possible. The winning
        // rung delivers the WHOLE run against a FIXED target — no mid-run
        // re-route (first refused chunk => SendFailed => pill).
        var capture = _focusedChildCapture(hwndAtSendStart);
        var selection = DeliveryLadder.Select(_channelOrder(), _strategies, hwndAtSendStart, capture);
        if (selection.GatesSummary.Length > 0)
        {
            _log.LogInformation(
                "Delivery ladder gated rungs out ({Gates}); delivering via {Via}",
                selection.GatesSummary, InjectionChannelNames.Name(selection.Strategy.Channel));
        }
        var targetHwnd = capture.FocusedChildHwnd; // 0 when unstable; VkPacket ignores it
```

**(e)** In the same method, change the `GuardedInjectionRun.Execute` call's fourth
argument from `_sendChunk,` to:

```csharp
            chunk => selection.Strategy.TrySendChunk(targetHwnd, chunk),
```

**(f)** Change the final return from
`return new InjectionRunReport(run.Outcome, chunks.Count, run.ChunksSent, nominalPacingMs);` to:

```csharp
        return new InjectionRunReport(
            run.Outcome, chunks.Count, run.ChunksSent, nominalPacingMs,
            selection.Strategy.Channel, selection.GatesSummary);
```

Nothing else in the file changes; in particular `SendChunkViaSendInput`,
`NeutralizeHeldModifiers`, the elevation/foreground/mouse blocks, and all pacing
code stay byte-identical. `GuardedInjectionRun.cs` is not opened at all.

- [ ] **Step 5: Run the new tests**

Run **[BUILD-PLATFORM]** then **[RUN-CLASS]** with `TextInjectorLadderTests`.
Expected: PASS.

- [ ] **Step 6: Mechanically update TextInjectorGuardedTests where needed**

Run **[RUN-CLASS]** with `TextInjectorGuardedTests` and with
`GuardedInjectionRunTests`. `GuardedInjectionRunTests` MUST pass unchanged (do not
edit that file). For `TextInjectorGuardedTests`, only these two mechanical changes
are permitted — halt/pacing/prelude pins must not change semantics:

1. Add a deterministic capture seam to the file's `NewInjector` factory so the
   suite behaves identically on the Windows TFM run (where the default capture
   would otherwise touch real Win32 and consume a 30 ms sleep):

```csharp
    private static TextInjector NewInjector(
        Func<long> foregroundHwnd,
        Func<string, bool> sendChunk)
        => new(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,          // no held modifiers => no wait, no modifier halt
            foregroundHwnd: foregroundHwnd,
            sendChunk: sendChunk,
            sleep: _ => { },                // no real pacing in unit tests
            focusedChildCapture: _ => new FocusedChildCapture(0, false)); // ladder => VkPacket floor
```

   Apply the same `focusedChildCapture: _ => new FocusedChildCapture(0, false)`
   named argument to any test in the file that constructs `TextInjector` directly
   (skip constructions whose run parks before routing, e.g. `foregroundHwnd: () => 0`
   — adding it there is harmless and acceptable too).

   Checklist (enumerated 2026-08-07 by load-bearing validation; re-run
   `grep -n "new TextInjector(" tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs`
   to confirm no drift): 19 construction sites at lines
   98, 159, 176, 199, 251, 282, 307, 327, 350, 387, 407, 425, 446, 467, 491, 515,
   533, 556, 576. Of these, 6 park before routing (98, 159, 307, 350, 387, 576 —
   exempt) and 13 reach routing and NEED the seam (176, 199, 251, 282, 327, 407,
   425, 446, 467, 491, 515, 533, 556). Every needed site must be covered before
   this step is complete.
2. If any assertion compares a whole `InjectionRunReport` value, rewrite it to
   assert the four original properties individually (`.Outcome`, `.ChunksTotal`,
   `.ChunksSent`, `.PacingWaitMs`) — do not weaken any of them.

Re-run **[RUN-CLASS]** with `TextInjectorGuardedTests`. Expected: PASS (28 tests).

- [ ] **Step 7: Verify GuardedInjectionRun is untouched**

```bash
git diff main -- src/Winpepper.Platform/Injection/GuardedInjectionRun.cs \
                 tests/Winpepper.Platform.Tests/Injection/GuardedInjectionRunTests.cs
```
Expected: empty output.

- [ ] **Step 8: Full suite + commit**

```bash
./scripts/linux-tests.sh   # LINUX SUITE: GREEN
git add src/Winpepper.Platform/Injection/InjectionRunReport.cs \
        src/Winpepper.Platform/Injection/TextInjector.cs \
        tests/Winpepper.Platform.Tests/Injection/TextInjectorLadderTests.cs \
        tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs
git commit -m "feat(injection): route injection through the delivery ladder with Via/GatesSummary provenance"
```

---

### Task 8: Settings — `injectionChannels` field + list-aware settings diff

**Files:**
- Modify: `src/Winpepper.Core/Settings/AppSettings.cs`
- Modify: `src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs`
- Modify: `src/Winpepper.Core/Winpepper.Core.csproj`
- Test: `tests/Winpepper.Core.Tests/AppSettingsDefaultsTests.cs` (add tests)
- Test: `tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs` (add tests)
- Test: `tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterDiffTests.cs` (create)

**Interfaces:**
- Consumes: nothing new (Core cannot reference Platform, so the raw strings live
  here and the `DeliveryChannel` parse happens Platform-side via
  `InjectionChannelNames.ParseLadder` — Task 10 wires them together).
- Produces: `AppSettings.InjectionChannels : IReadOnlyList<string>` (camelCase JSON
  key `injectionChannels` via the store's existing naming policy — no attribute
  needed); `internal static bool DebouncedSettingsWriter.PropertyValuesEqual(object?, object?)`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Winpepper.Core.Tests/AppSettingsDefaultsTests.cs` (inside the
existing test class, following its existing `Defaults_*` style):

```csharp
    [Fact]
    public void Defaults_InjectionChannels_IsFullLadderOrder()
    {
        new AppSettings().InjectionChannels.ShouldBe(
            new[] { "emReplaceSel", "wmCharSmto", "vkPacket" });
    }
```

Add to `tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs` (inside the
existing class, using its existing per-test `_path` temp-file convention — mirror
the setup of the existing `StreamingEnabled_MissingFromOlderFile_DefaultsTrue`
test exactly, including how the store is constructed and how old-shape JSON is
written; and mirror the file's existing save→load round-trip test for the second
test's store API):

```csharp
    [Fact]
    public void InjectionChannels_MissingFromOlderFile_DefaultsToFullLadder()
    {
        File.WriteAllText(_path, """{ "schema": 1 }""");

        var settings = new SettingsStore(_path).Load();

        settings.InjectionChannels.ShouldBe(new[] { "emReplaceSel", "wmCharSmto", "vkPacket" });
    }

    [Fact]
    public void InjectionChannels_CustomOrder_RoundTrips()
    {
        var store = new SettingsStore(_path);
        store.Save(new AppSettings { InjectionChannels = new[] { "vkPacket" } });

        store.Load().InjectionChannels.ShouldBe(new[] { "vkPacket" });
    }
```

Create `tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterDiffTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests.Settings;

public class DebouncedSettingsWriterDiffTests
{
    [Fact]
    public void PropertyValuesEqual_StringSequences_CompareByContent()
    {
        // A freshly deserialized list must not mis-diff as "changed" just
        // because it is a different instance.
        DebouncedSettingsWriter.PropertyValuesEqual(
            new[] { "a", "b" }, new List<string> { "a", "b" }).ShouldBeTrue();
        DebouncedSettingsWriter.PropertyValuesEqual(
            new[] { "a" }, new[] { "b" }).ShouldBeFalse();
        DebouncedSettingsWriter.PropertyValuesEqual(
            new[] { "a", "b" }, new[] { "b", "a" }).ShouldBeFalse(); // order matters (it is a ladder)
    }

    [Fact]
    public void PropertyValuesEqual_Scalars_UseEquals()
    {
        DebouncedSettingsWriter.PropertyValuesEqual(5, 5).ShouldBeTrue();
        DebouncedSettingsWriter.PropertyValuesEqual("x", "x").ShouldBeTrue();
        DebouncedSettingsWriter.PropertyValuesEqual(null, null).ShouldBeTrue();
        DebouncedSettingsWriter.PropertyValuesEqual(5, 6).ShouldBeFalse();
        DebouncedSettingsWriter.PropertyValuesEqual("x", null).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run **[BUILD-CORE]**. Expected: FAIL — `InjectionChannels` and
`PropertyValuesEqual` don't exist (and `PropertyValuesEqual` is not visible).

- [ ] **Step 3: Implement**

**(a)** In `src/Winpepper.Core/Settings/AppSettings.cs`, append after the last
property (`public int? WindowHeight { get; init; }`, currently line 95), before
the closing brace:

```csharp
    /// <summary>Injection delivery-ladder order (design doc 2026-08-06 §2.3).
    /// Channel names, case-insensitive: "emReplaceSel", "wmCharSmto",
    /// "vkPacket". Unknown names are logged and skipped at parse time; an
    /// empty or fully-invalid list falls back to this hardcoded default.
    /// Parsing lives in Winpepper.Platform.Injection.InjectionChannelNames
    /// (Core cannot reference Platform's DeliveryChannel type). First
    /// collection-typed settings property — DebouncedSettingsWriter's diff
    /// special-cases string sequences for it.</summary>
    public IReadOnlyList<string> InjectionChannels { get; init; } =
        new[] { "emReplaceSel", "wmCharSmto", "vkPacket" };
```

**(b)** In `src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs`, inside
`LogChangedFields` (lines ~157–174), change the diff predicate from

```csharp
            .Where(p => !Equals(p.GetValue(before), p.GetValue(after)))
```

to

```csharp
            .Where(p => !PropertyValuesEqual(p.GetValue(before), p.GetValue(after)))
```

update the nearby inline comment that says all properties are scalar (it now reads
"all properties are scalar except InjectionChannels; PropertyValuesEqual
sequence-compares string collections"), and add this method to the class:

```csharp
    /// <summary>Value comparison for the change-diff log. Scalars use
    /// Equals; string collections (InjectionChannels) compare by sequence so
    /// a rebuilt-but-content-equal list does not mis-diff as changed.
    /// Internal for direct unit testing.</summary>
    internal static bool PropertyValuesEqual(object? a, object? b)
    {
        if (a is IEnumerable<string> ea && b is IEnumerable<string> eb)
            return ea.SequenceEqual(eb);
        return Equals(a, b);
    }
```

**(c)** In `src/Winpepper.Core/Winpepper.Core.csproj`, add (inside an existing
`<ItemGroup>` or a new one, mirroring how `src/Winpepper.Platform/Winpepper.Platform.csproj`
declares it):

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Winpepper.Core.Tests" />
  </ItemGroup>
```

**(d)** Guard check for other value-equality consumers of `AppSettings` (records
compare `IReadOnlyList<string>` by reference):

```bash
grep -rn "DebouncedSettingsWriter\|AppSettings" src/ --include=*.cs | grep -E "\.Equals\(|== *[a-z_]*[Ss]ettings"
```

Decision rule: if any hit compares two `AppSettings` instances for value equality
(record `==`/`.Equals`), route it through the same sequence-aware comparison or
flag it in the commit message; if there are no such hits (expected), proceed.

- [ ] **Step 4: Run to verify pass**

Run **[BUILD-CORE]** then **[RUN-CORE]** with each of
`Winpepper.Core.Tests.AppSettingsDefaultsTests`,
`Winpepper.Core.Tests.Settings.SettingsStoreTests`,
`Winpepper.Core.Tests.Settings.DebouncedSettingsWriterDiffTests`. Expected: PASS.

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh   # LINUX SUITE: GREEN
git add src/Winpepper.Core/Settings/AppSettings.cs \
        src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs \
        src/Winpepper.Core/Winpepper.Core.csproj \
        tests/Winpepper.Core.Tests/AppSettingsDefaultsTests.cs \
        tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs \
        tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterDiffTests.cs
git commit -m "feat(settings): injectionChannels ladder-order field with list-aware change diff"
```

---

### Task 9: Telemetry — `InjectVia`/`InjectGates` on DictationTimingSummary

**Files:**
- Modify: `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs`
- Test: `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs`

**Interfaces:**
- Consumes: nothing (plain strings; the channel-name mapping happens at the
  PipelineHost stamp site in Task 10).
- Produces: `public string? InjectVia { get; set; }`,
  `public string? InjectGates { get; set; }`, rendered as
  `inject_via=<name>` immediately after `inject_chunks=` and
  `inject_gates=<comma-list>` immediately after `inject_via` (both before
  `inject_pace=`), omitted when null (and `inject_gates` also when empty).

- [ ] **Step 1: Write the failing tests**

Add to the existing test class in
`tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs`:

```csharp
    [Fact]
    public void FormatLine_RendersInjectVia_BetweenInjectChunksAndInjectPace()
    {
        var t = new DictationTimingSummary
        {
            SessionId = Guid.NewGuid(),
            Kind = "hold",
            Outcome = "completed",
            InjectChunksSent = 3,
            InjectChunksTotal = 3,
            InjectPacingMs = 28,
            InjectVia = "emReplaceSel",
        };
        var line = t.FormatLine();

        line.ShouldContain("inject_chunks=3/3 inject_via=emReplaceSel inject_pace=28ms");
    }

    [Fact]
    public void FormatLine_RendersInjectGates_ImmediatelyAfterInjectVia()
    {
        var t = new DictationTimingSummary
        {
            SessionId = Guid.NewGuid(),
            Kind = "hold",
            Outcome = "completed",
            InjectChunksSent = 3,
            InjectChunksTotal = 3,
            InjectVia = "vkPacket",
            InjectGates = "emReplaceSel:no-em,wmCharSmto:focus-unstable",
        };
        var line = t.FormatLine();

        line.ShouldContain(
            "inject_via=vkPacket inject_gates=emReplaceSel:no-em,wmCharSmto:focus-unstable");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FormatLine_OmitsInjectGates_WhenNullOrEmpty(string? gates)
    {
        var t = new DictationTimingSummary
        {
            SessionId = Guid.NewGuid(),
            Kind = "hold",
            Outcome = "completed",
            InjectVia = "emReplaceSel",
            InjectGates = gates,
        };
        t.FormatLine().ShouldNotContain("inject_gates");
    }

    [Fact]
    public void FormatLine_OmitsInjectVia_WhenNull()
    {
        var t = new DictationTimingSummary { SessionId = Guid.Empty, Kind = "hold", Outcome = "empty" };
        t.FormatLine().ShouldNotContain("inject_via");
    }
```

- [ ] **Step 2: Run to verify failure**

Run **[BUILD-CORE]**. Expected: FAIL with CS0117/CS1061 (`InjectVia` not found).

- [ ] **Step 3: Implement**

In `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs`:

**(a)** Add two properties directly after `public int? InjectPacingMs { get; set; }`:

```csharp
    /// <summary>Delivery channel that carried the injection run, camelCase
    /// channel name ("emReplaceSel" | "wmCharSmto" | "vkPacket") — design
    /// doc 2026-08-06 §2.4 provenance; null when no delivery was attempted.</summary>
    public string? InjectVia { get; set; }

    /// <summary>Rungs that gated out and why, e.g.
    /// "emReplaceSel:no-em,wmCharSmto:focus-unstable"; null/empty (omitted)
    /// when the first rung delivered. Makes field routing regressions
    /// diagnosable from the log alone.</summary>
    public string? InjectGates { get; set; }
```

**(b)** In `FormatLine()`, change the inject block from

```csharp
        if (InjectChunksSent is not null || InjectChunksTotal is not null)
            sb.Append(" inject_chunks=").Append(InjectChunksSent ?? 0).Append('/').Append(InjectChunksTotal ?? 0);
        AppendOptMs(sb, "inject_pace", InjectPacingMs);
```

to

```csharp
        if (InjectChunksSent is not null || InjectChunksTotal is not null)
            sb.Append(" inject_chunks=").Append(InjectChunksSent ?? 0).Append('/').Append(InjectChunksTotal ?? 0);
        AppendOptStr(sb, "inject_via", InjectVia);
        AppendOptStr(sb, "inject_gates", string.IsNullOrEmpty(InjectGates) ? null : InjectGates);
        AppendOptMs(sb, "inject_pace", InjectPacingMs);
```

- [ ] **Step 4: Run to verify pass, then reconcile the golden-line test**

Run **[BUILD-CORE]** then **[RUN-CORE]** with
`Winpepper.Core.Tests.Diagnostics.DictationTimingSummaryTests`. The new tests
pass; the existing golden test `FormatLine_FullDictation_IsOneParseableKeyValueLine`
still passes because its `Full()` fixture leaves the new fields null (omitted).
Strengthen it to cover the new fields: in the `Full()` fixture add, next to the
existing `InjectChunksSent = 58` block,

```csharp
        InjectVia = "emReplaceSel",
```

and in the expected string change the fragment
`" inject=850ms inject_chars=458 inject_chunks=58/58 inject_pace=798ms"` to
`" inject=850ms inject_chars=458 inject_chunks=58/58 inject_via=emReplaceSel inject_pace=798ms"`.
Re-run the class. Expected: PASS.

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh   # LINUX SUITE: GREEN
git add src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs \
        tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs
git commit -m "feat(telemetry): inject_via and inject_gates in the dictation timing line"
```

---

### Task 10: PipelineHost wiring — settings-driven ladder order + telemetry stamps

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs`

**Interfaces:**
- Consumes: `TextInjector` public ctor `channelOrder` seam (Task 7),
  `InjectionChannelNames.ParseLadder/Name` (Task 1),
  `AppSettings.InjectionChannels` (Task 8),
  `DictationTimingSummary.InjectVia/InjectGates` (Task 9),
  `InjectionRunReport.Via/GatesSummary` (Task 7).
- Produces: production wiring only; no new surface.

**Verification note:** `Winpepper.App` is WinUI and does not compile on Linux —
the Linux suite proves nothing else broke; the compile/behavior proof for this
task lands with the Windows gate (Task 12). Keep every edit mechanical and
minimal. The file already has `using Winpepper.Platform.Injection;`, so
`InjectionChannelNames` needs no qualification; other injection types in this file
are conventionally fully qualified — follow whichever the touched line already
uses.

- [ ] **Step 1: Wire the settings-driven ladder order into the injector**

At `PipelineHost.cs` line ~181, replace

```csharp
        _injector = new TextInjector(factory.CreateLogger<TextInjector>());
```

with

```csharp
        // Ladder order is re-read per injection run (the lambda defers to
        // the settings provider), so a settings.json reorder/removal takes
        // effect without an app restart — the design's field-regression
        // recovery story. Unknown names warn-and-skip; empty/invalid lists
        // fall back to the hardcoded default inside ParseLadder.
        _injector = new TextInjector(
            factory.CreateLogger<TextInjector>(),
            channelOrder: () => InjectionChannelNames.ParseLadder(
                _settingsProvider().InjectionChannels,
                unknown => _log.LogWarning(
                    "Unknown injectionChannels entry '{Name}' in settings; skipping", unknown)));
```

(`_settingsProvider` is the existing `private readonly Func<AppSettings>` field —
assigned later in the ctor than this line, which is fine: the lambda is evaluated
per run, never during construction.)

- [ ] **Step 2: Stamp the HoldUp arm** (call site ~lines 1031–1038, the
`injReport`/`timing` pair)

Replace

```csharp
                        if (injReport.ChunksTotal > 0)
                        {
                            timing.InjectChunksSent = injReport.ChunksSent;
                            timing.InjectChunksTotal = injReport.ChunksTotal;
                            timing.InjectPacingMs = injReport.PacingWaitMs;
                        }
```

with

```csharp
                        if (injReport.ChunksTotal > 0)
                        {
                            timing.InjectChunksSent = injReport.ChunksSent;
                            timing.InjectChunksTotal = injReport.ChunksTotal;
                            timing.InjectPacingMs = injReport.PacingWaitMs;
                            timing.InjectVia = InjectionChannelNames.Name(injReport.Via);
                            if (!string.IsNullOrEmpty(injReport.GatesSummary))
                                timing.InjectGates = injReport.GatesSummary;
                        }
```

- [ ] **Step 3: Stamp the toggle-stop arm** (call site ~lines 1659–1665, the
`injReport2`/`timing2` twin)

Apply the identical change to the `if (injReport2.ChunksTotal > 0)` block, using
`timing2.InjectVia = InjectionChannelNames.Name(injReport2.Via);` and
`timing2.InjectGates = injReport2.GatesSummary;` under the same
`!string.IsNullOrEmpty` guard.

- [ ] **Step 4: TryPastePending** — it reports NO timing summary (verified), so
its stamp is its existing success log line (~line 545). Replace

```csharp
        if (injected)
            _log.LogInformation(
                "Pending paste injected ({Chars} chars, {ChunksSent}/{ChunksTotal} chunks, nominal pacing {PacingMs} ms)",
                text.Length, report.ChunksSent, report.ChunksTotal, report.PacingWaitMs);
```

with

```csharp
        if (injected)
            _log.LogInformation(
                "Pending paste injected ({Chars} chars, {ChunksSent}/{ChunksTotal} chunks, nominal pacing {PacingMs} ms, via {Via})",
                text.Length, report.ChunksSent, report.ChunksTotal, report.PacingWaitMs,
                InjectionChannelNames.Name(report.Via));
```

- [ ] **Step 5: Sanity-check the diff, run the suite, commit**

```bash
git diff src/Winpepper.App/Hosting/PipelineHost.cs   # exactly the four edits above
./scripts/linux-tests.sh                             # LINUX SUITE: GREEN
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat(app): settings-driven delivery-ladder order and inject_via/inject_gates stamps"
```

---

### Task 11: Windows-gate in-proc tests — hosted EDIT child + capture stability

**Files:**
- Create: `tests/Winpepper.Platform.Tests/Injection/NativeEditHost.cs`
- Create: `tests/Winpepper.Platform.Tests/Injection/DeliveryStrategyWindowsTests.cs`

**Interfaces:**
- Consumes: `EmReplaceSelStrategy`, `WmCharSmtoStrategy`, `MessageDelivery`,
  `FocusedChildProbe`, `InjectionChunker.Split` (existing public static).
- Produces: `NativeEditHost` test helper — `Start()` (pumping) /
  `StartNonPumping()`, `ParentHwnd`, `EditHwnd`, `ReadText()`, `IDisposable`.
  There is NO existing window-hosting helper in the repo; this is new,
  deliberately test-only infrastructure.

These tests compile on both TFMs but are excluded from Linux runs by
`[Trait("Platform", "Windows")]` (the `linux-tests.sh -notrait` filter) and
self-guard with `if (!OperatingSystem.IsWindows()) return;`. They EXECUTE only
under `./scripts/windows-gate.sh` (Task 12).

- [ ] **Step 1: Write the test helper**

Create `tests/Winpepper.Platform.Tests/Injection/NativeEditHost.cs`:

```csharp
using System.Runtime.InteropServices;

namespace Winpepper.Platform.Tests.Injection;

/// <summary>
/// Hosts a real Win32 EDIT control on a dedicated STA thread with a message
/// pump, for in-proc delivery-strategy tests (design doc §4 Windows gate).
/// StartNonPumping creates the same windows but never pumps — the target for
/// the "SMTO must return false within &lt;= 2x timeout" pipeline-never-hangs
/// pin. Uses built-in window classes (STATIC parent, EDIT child) so no class
/// registration is needed. Windows-only; callers self-guard.
/// </summary>
internal sealed partial class NativeEditHost : IDisposable
{
    public IntPtr ParentHwnd { get; private set; }
    public IntPtr EditHwnd { get; private set; }
    public uint ThreadId { get; private set; }

    private readonly bool _pump;
    private readonly ManualResetEventSlim _ready = new();
    private readonly ManualResetEventSlim _stop = new();
    private Thread? _thread;

    private NativeEditHost(bool pump) => _pump = pump;

    public static NativeEditHost Start() => Launch(pump: true);

    public static NativeEditHost StartNonPumping() => Launch(pump: false);

    private static NativeEditHost Launch(bool pump)
    {
        var host = new NativeEditHost(pump);
        host._thread = new Thread(host.Run) { IsBackground = true, Name = "NativeEditHost" };
        host._thread.SetApartmentState(ApartmentState.STA);
        host._thread.Start();
        if (!host._ready.Wait(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("NativeEditHost failed to start within 10 s");
        return host;
    }

    private void Run()
    {
        ThreadId = GetCurrentThreadId();
        ParentHwnd = CreateWindowExW(0, "STATIC", "winpepper-edit-host",
            WS_OVERLAPPED, 0, 0, 400, 200, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        EditHwnd = CreateWindowExW(0, "EDIT", "",
            WS_CHILD | WS_VISIBLE | ES_MULTILINE, 0, 0, 380, 180,
            ParentHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        SetFocus(EditHwnd); // thread-local keyboard focus: GetGUIThreadInfo sees it
        _ready.Set();
        if (!_pump)
        {
            _stop.Wait(); // deliberately never pump: SMTO sends must time out
            return;
        }
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(in msg);
            DispatchMessageW(in msg);
        }
    }

    /// <summary>Read the EDIT content (cross-thread WM_GETTEXT; requires the pumping host).</summary>
    public string ReadText()
    {
        var buffer = new char[4096];
        var length = GetWindowTextW(EditHwnd, buffer, buffer.Length);
        return new string(buffer, 0, length);
    }

    public void Dispose()
    {
        if (_pump && ThreadId != 0)
            PostThreadMessageW(ThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _stop.Set();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
        _stop.Dispose();
    }

    private const uint WS_OVERLAPPED = 0x00000000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint ES_MULTILINE = 0x0004;
    private const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetFocus(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in MSG lpMsg);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DispatchMessageW(in MSG lpMsg);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowTextW(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);
}
```

- [ ] **Step 2: Write the Windows tests**

Create `tests/Winpepper.Platform.Tests/Injection/DeliveryStrategyWindowsTests.cs`:

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

[Trait("Platform", "Windows")]
public class DeliveryStrategyWindowsTests
{
    // Production string shape: ASCII + surrogate pair (G-clef) + accents.
    private const string Payload = "Even we worked \uD834\uDD1E caf\u00E9 done.";

    [Fact]
    public void EmReplaceSel_DeliversChunksVerbatim_InOrder_ToHostedEdit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = NativeEditHost.Start();
        var strategy = new EmReplaceSelStrategy(NullLogger.Instance);
        var target = host.EditHwnd.ToInt64();

        strategy.CanDeliver(host.ParentHwnd.ToInt64(), target).ShouldBeTrue();
        foreach (var chunk in InjectionChunker.Split(Payload, TextInjector.ChunkCodeUnits))
            strategy.TrySendChunk(target, chunk).ShouldBeTrue();

        host.ReadText().ShouldBe(Payload); // verbatim, in order, surrogates intact
    }

    [Fact]
    public void WmCharSmto_DeliversUnitsVerbatim_InOrder_ToHostedEdit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = NativeEditHost.Start();
        var strategy = new WmCharSmtoStrategy(NullLogger.Instance);
        var target = host.EditHwnd.ToInt64();

        strategy.CanDeliver(host.ParentHwnd.ToInt64(), target).ShouldBeTrue();
        foreach (var chunk in InjectionChunker.Split(Payload, TextInjector.ChunkCodeUnits))
            strategy.TrySendChunk(target, chunk).ShouldBeTrue();

        host.ReadText().ShouldBe(Payload);
    }

    [Fact]
    public void EmReplaceSelGate_PassesOnHostedEditClass()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = NativeEditHost.Start();

        var cls = MessageDelivery.ClassName(host.EditHwnd.ToInt64());
        cls.ShouldNotBeNull();
        cls.ShouldContain("Edit", Case.Insensitive);
        MessageDelivery.EmGetSelProbe(host.EditHwnd.ToInt64()).ShouldBeTrue();
    }

    [Fact]
    public void DestroyedHwnd_GateAndSend_FailLoudlyFalse()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = NativeEditHost.Start();
        var target = host.EditHwnd.ToInt64();
        var foreground = host.ParentHwnd.ToInt64();
        host.Dispose(); // windows destroyed with their thread

        var em = new EmReplaceSelStrategy(NullLogger.Instance);
        var wm = new WmCharSmtoStrategy(NullLogger.Instance);
        em.CanDeliver(foreground, target).ShouldBeFalse();
        em.TrySendChunk(target, "x").ShouldBeFalse();
        wm.TrySendChunk(target, "x").ShouldBeFalse();
    }

    [Fact]
    public void NonPumpingWindow_TrySendChunk_ReturnsFalse_WithinTwiceTheTimeout()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = NativeEditHost.StartNonPumping();
        var strategy = new WmCharSmtoStrategy(NullLogger.Instance);

        var sw = Stopwatch.StartNew();
        var ok = strategy.TrySendChunk(host.EditHwnd.ToInt64(), "x"); // one unit => one SMTO call
        sw.Stop();

        ok.ShouldBeFalse();
        // Pipeline-never-hangs pin: <= 2x the 150 ms SMTO timeout.
        sw.ElapsedMilliseconds.ShouldBeLessThanOrEqualTo(300);
    }

    [Fact]
    public void NonPumpingWindow_EmReplaceSel_ChunkSend_AlsoBounded()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = NativeEditHost.StartNonPumping();
        var strategy = new EmReplaceSelStrategy(NullLogger.Instance);

        var sw = Stopwatch.StartNew();
        var ok = strategy.TrySendChunk(host.EditHwnd.ToInt64(), "hello wo"); // one chunk => one SMTO call
        sw.Stop();

        ok.ShouldBeFalse();
        sw.ElapsedMilliseconds.ShouldBeLessThanOrEqualTo(300);
    }

    [Fact]
    public void FocusedChildProbe_DoubleSample_IsStable_OnInProcWindow()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = NativeEditHost.Start();

        var capture = FocusedChildProbe.Capture(
            host.ParentHwnd.ToInt64(), MessageDelivery.SampleFocusedChild, Thread.Sleep);

        capture.Stable.ShouldBeTrue();
        capture.FocusedChildHwnd.ShouldBe(host.EditHwnd.ToInt64());
    }
}
```

(If Shouldly's string overload set differs, `cls.ToLowerInvariant().ShouldContain("edit")`
is the acceptable equivalent for the class-name assertion.)

- [ ] **Step 3: Verify Linux behavior** (compiles; excluded from the run)

Run **[BUILD-PLATFORM]** — expected: build succeeds. Then run the full DLL with
the standard filter and confirm the new class does not execute:

```bash
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll \
  -class "Winpepper.Platform.Tests.Injection.DeliveryStrategyWindowsTests" -notrait "Platform=Windows"
```

Expected: 0 tests run (all excluded by trait).

- [ ] **Step 4: Full suite + commit**

```bash
./scripts/linux-tests.sh   # LINUX SUITE: GREEN
git add tests/Winpepper.Platform.Tests/Injection/NativeEditHost.cs \
        tests/Winpepper.Platform.Tests/Injection/DeliveryStrategyWindowsTests.cs
git commit -m "test(injection): Windows in-proc EDIT-host delivery and capture-stability tests"
```

The actual Windows execution of these tests happens in Task 12's gate run; if the
gate finds failures, fix them in this task's files (and only here unless the gate
proves a production-code defect), re-run the Linux suite, and commit the fix.

---

### Task 12: Close-out — design-doc implementation notes, zero-diff pin, Windows gate

**Files:**
- Modify (append only): `docs/designs/2026-08-06-injection-delivery-integrity-v3.md`

- [ ] **Step 1: Append the implementation-notes section to the design doc**

Append EXACTLY this at the end of
`docs/designs/2026-08-06-injection-delivery-integrity-v3.md` (the spec above it
stays frozen):

```markdown

## 6. Implementation notes (2026-08-07, appended by the implementation)

Gap resolutions pinned by `docs/plans/2026-08-07-injection-delivery-ladder.md`
(none change the accepted architecture; recorded here so the doc and the code
agree):

- `DeliveryChannel` is declared with `VkPacket = 0` so
  `default(InjectionRunReport).Via` reads as the status-quo floor.
- Stability plumbing: `FocusedChildCapture(FocusedChildHwnd, Stable)` with
  `!Stable => FocusedChildHwnd == 0`; `CanDeliver` receives that effective
  hwnd, which is how the two-`long` contract "receives the stability fact".
- Gate-out reasons are derived by the router (the gate returns only bool):
  `focus-unstable` when the capture was unstable/zero, else `no-em`.
- The §2.4 example rung name `wmCharFenced` and §3's `inject_via=wmchar` are
  treated as typos; the canonical spelling everywhere is `wmCharSmto`.
- "Rung 4" in §2.2/§2.5 is read as rung 3 (`VkPacket`) — the table defines
  three rungs.
- If a settings-configured ladder exhausts with no passing gate (only
  possible when `vkPacket` was removed), delivery degrades to the VkPacket
  floor and the gates record still lists the gated rungs (§3: "rungs degrade
  to the VK_PACKET floor = status quo").
- The capture + ladder walk run after the elevation check AND the
  modifier/mouse release preludes, immediately before chunking.
- A zero FIRST focus sample short-circuits the capture as unstable without
  the 30 ms gap (outcome-equivalent to sampling twice).
- Telemetry: `inject_via=` renders immediately after `inject_chunks=`,
  `inject_gates=` immediately after `inject_via=`; both are stamped under
  the existing `ChunksTotal > 0` guard. `TryPastePending` has no timing
  summary; its provenance is `via <channel>` on its existing success log
  line. Duplicate settings entries de-duplicate (first occurrence wins).

Known residual risks surfaced by the plan's load-bearing validation (accepted;
none changes the architecture — details in the plan's "Known residual risks"):

- Rungs 1–2 deliver every chunk to the ONE focused-child hwnd captured at send
  start; the shared halt observes only the top-level foreground window, so a
  mid-run focus move to a *different child in the same window* is not detected.
- A `SendMessageTimeout` `false` (150 ms, SMTO_ABORTIFHUNG) does not guarantee
  non-delivery — a slow (not hung) receiver may process the chunk later. The
  pinned no-reroute/no-auto-retry behavior on `SendFailed` is therefore
  LOAD-BEARING: never add automated retry for a message-based rung without
  content de-duplication. Residual: a manual retry after the pill can duplicate.
- Rung 2's worst-case per-chunk send is ~8–9 × 150 ms ≈ 1.35 s against a
  slow/degraded target (healthy targets: ~µs per WM_CHAR), wider than the
  microsecond-scale exposure window the guarded-run comment assumes; halts stay
  at chunk boundaries. Accepted as the rung-2 correctness/liveness trade-off.
```

- [ ] **Step 2: GuardedInjectionRun zero-diff pin (spec §4 requirement)**

```bash
git diff main -- src/Winpepper.Platform/Injection/GuardedInjectionRun.cs
git diff main -- tests/Winpepper.Platform.Tests/Injection/GuardedInjectionRunTests.cs
git log --oneline main..HEAD -- src/Winpepper.Platform/Injection/GuardedInjectionRun.cs
```

Expected: all three commands print NOTHING. If any prints a diff/commit, the run
loop was touched — revert that change and rework the offending task without it.

- [ ] **Step 3: Full Linux suite + commit the doc note**

```bash
./scripts/linux-tests.sh   # LINUX SUITE: GREEN
git add docs/designs/2026-08-06-injection-delivery-integrity-v3.md
git commit -m "docs(designs): append delivery-ladder implementation notes (gap resolutions)"
```

- [ ] **Step 4: Windows gate (required before ANYTHING is pushed/merged)**

```bash
./scripts/windows-gate.sh   # run with a 20-40 minute timeout; from WSL
```

Expected: `GATE: GREEN`. This is where Task 11's `[Trait("Platform","Windows")]`
tests actually execute (both TFMs, 12 runs) and where PipelineHost (Task 10)
first compiles. If the gate is RED: fix, get `./scripts/linux-tests.sh` green,
commit the fix, and re-run the gate until GREEN. Note: the gate cleans `bin/obj`
cross-OS state, so the next Linux run rebuilds from scratch — never run both
scripts concurrently. Do NOT push from the worktree; the merge happens from the
main checkout after this workflow completes.

- [ ] **Step 5: Post-implementation validation note (not automatable here)**

The design doc §4 lists post-implementation steps that are OUTSIDE this plan's
automated scope and are NOT to be simulated: one live E6-shape confirmation run
per default-ladder rung against the real binary, then the first-week
`inject_via`/`inject_gates` field review. They remain with the owner; nothing to
code.

The load-bearing validation pass (2026-08-07) enumerated exactly what those owner
runs/reviews must cover, because it is the coverage no automated test in this plan
provides (see "Known residual risks"):

- **Rung 1 on a real `RichEditD2DPT`** (Win11 Notepad): verbatim, in-order,
  surrogates-intact delivery — Task 11 only proves a synthetic classic `EDIT`.
- **Rung 2 on Chromium (`Chrome_WidgetWin_1`) and Windows Terminal
  (`InputSite.WindowClass`)**: these classes gate OUT of rung 1 by design (no
  "edit" in the class name) and ride rung 2, whose real-target evidence is n=1–2
  probe runs.
- **`inject_gates` review for capture failures**: `focus-unstable` firing against
  ordinary targets would indicate the cross-process / elevated-target
  `GetGUIThreadInfo` capture degrading (feature silently neutered to the VkPacket
  floor — status quo, not corruption, but worth catching).
- **`inject_via=emReplaceSel` false-positive watch**: any "edit"-named third-party
  control that mis-handles EM_REPLACESEL (the E9a sample was 4 apps; EM_GETSEL is
  non-discriminating — DefWindowProc answers it — so the gate rests on the class-name
  heuristic).
- **`SendFailed` occurrences on message-based rungs**: check the target document for
  late-arriving duplicate text before/after any manual retry (residual R2).

---

## Self-review record

Checked against the design doc (v3.2) and the task spec:

1. **Spec coverage:** strategy contract → Task 4; three rungs with exact
   gates/sends/constants → Tasks 4–5 (+3 for Win32); capture hardening → Tasks 2,
   3, 7; routing (first-gate-wins, once, fixed target, no re-route, SendFailed
   mapping) → Tasks 6–7; settings field with warn/skip/default → Tasks 1, 8, 10;
   telemetry (Via default VkPacket, GatesSummary, inject_via/inject_gates
   placement, AppendOptStr pattern, PipelineHost stamps incl. TryPastePending
   disposition) → Tasks 7, 9, 10; Win32 plumbing behind seams → Tasks 3–5, 7;
   every listed Linux test → Tasks 1–9 test files; every listed Windows-gate test
   (EDIT-child verbatim order + surrogates, EM_GETSEL gate on EDIT, destroyed
   hwnd, non-pumping ≤2× timeout, double-sample stability) → Task 11;
   GuardedInjectionRun zero-diff → Task 7 step 7 + Task 12 step 2; forbidden
   rungs (fenced WM_CHAR, clipboard) excluded by Global Constraints and pinned by
   a parse-rejection test in Task 1; doc append-only note → Task 12.
2. **No silent deferrals:** every seam/fake used in Linux tests (fake strategies,
   seamed samplers/senders) has its production counterpart wired as the ctor
   default (Tasks 3–5, 7) and proven against real Win32 EDIT controls in Task 11
   with the gate run in Task 12 — no stub stands in for shipped behavior. The
   §4 "post-implementation live confirmation runs" are operational owner steps
   per the design doc itself (Task 12 step 5 names them; nothing is silently
   dropped).
3. **Type consistency:** `FocusedChildCapture(long FocusedChildHwnd, bool Stable)`,
   `DeliverySelection(IDeliveryStrategy Strategy, string GatesSummary)`,
   `InjectionRunReport(..., DeliveryChannel Via = DeliveryChannel.VkPacket,
   string? GatesSummary = null)`, `InjectionChannelNames.ParseLadder(IReadOnlyList<string>?,
   Action<string>?)`, and the TextInjector ctor seam names are used identically
   across Tasks 1–11 (verified by re-reading each Consumes/Produces block).
4. **Load-bearing validation pass (2026-08-07):** 10 assumptions surfaced and
   dispositioned — 4 verified (Win32 constants + wParam/lParam semantics vs. MS
   Learn; the exact Task-3 `LibraryImport` surface compiles on net9.0 via a /tmp
   probe, fallback unnecessary, `AllowUnsafeBlocks` precondition already met; the
   19 `TextInjectorGuardedTests` construction sites enumerated with a 13-site
   needs-seam checklist; no `AppSettings` whole-record equality consumer exists),
   3 falsified-and-planned-around (stale-child fixed target R1, SMTO
   timeout-then-late-delivery R2, rung-2 exposure window R3 — see "Known residual
   risks"), 3 accepted as gate/owner-deferred residuals (real-target rung
   correctness, "edit"-gate generalization, cross-process/elevated capture — Task
   12 step 5 now enumerates the required owner coverage). No edit changed any
   task's interfaces, test code, or commands; the changes are documentation
   (residual risks, Task 3 validation note, Task 7 step-6 checklist, Task 12
   step-1 appended notes and step-5 owner checklist), so the plan's task
   spec-coverage, no-silent-deferral, and type-consistency reviews (items 1–3)
   remain valid as written. Evidence ledger:
   `.worktrees/.the-usual-logs/injection-delivery-ladder/load-bearing-ledger.md`.
```
