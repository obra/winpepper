using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class TextInjectorGuardedTests
{
    private static TextInjector NewInjector(
        Func<long> foregroundHwnd,
        Func<string, bool> sendChunk)
        => new(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,          // no held modifiers => no wait, no modifier halt
            foregroundHwnd: foregroundHwnd,
            sendChunk: sendChunk,
            sleep: _ => { });               // no real pacing in unit tests

    [Fact]
    public void Guarded_StableFocus_SendsWholeText_InChunks()
    {
        var sent = new List<string>();
        var injector = NewInjector(() => 42, c => { sent.Add(c); return true; });
        var text = new string('a', 80);

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        string.Concat(sent).ShouldBe(text);
        sent.Count.ShouldBe(10); // ChunkCodeUnits = 8 => ten chunks of 8
    }

    [Fact]
    public void Guarded_FocusChange_MidSend_Interrupts_AndStopsSending()
    {
        var sent = new List<string>();
        var probes = 0;
        // Probe call 1 = entry baseline (42). Call 2 = check before chunk 1
        // (42 -> sends). Call 3 = check before chunk 2 (99 -> halts).
        var injector = NewInjector(
            () => ++probes <= 2 ? 42L : 99L,
            c => { sent.Add(c); return true; });
        var text = new string('a', 96); // 12 chunks of 8

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Interrupted);

        sent.Count.ShouldBe(1);
    }

    [Fact]
    public void Guarded_FocusChange_DuringModifierWait_SendsNothing()
    {
        // Baseline is taken at method ENTRY; if focus moves before the first
        // chunk check (e.g. during the modifier-release wait), nothing sends.
        var sent = new List<string>();
        var probes = 0;
        var injector = NewInjector(
            () => ++probes == 1 ? 42L : 99L,
            c => { sent.Add(c); return true; });

        injector.TryInjectGuarded("hello world").ShouldBe(InjectionRunOutcome.Interrupted);

        sent.ShouldBeEmpty();
    }

    [Fact]
    public void Guarded_SendRefused_ReturnsSendFailed()
    {
        var injector = NewInjector(() => 42, _ => false);

        injector.TryInjectGuarded("hello").ShouldBe(InjectionRunOutcome.SendFailed);
    }

    [Fact]
    public void Guarded_EmptyText_Completes_WithoutSending()
    {
        var injector = NewInjector(
            () => throw new InvalidOperationException("must not probe"),
            _ => throw new InvalidOperationException("must not send"));

        injector.TryInjectGuarded(string.Empty).ShouldBe(InjectionRunOutcome.Completed);
    }

    [Fact]
    public void Guarded_ZeroForegroundAtStart_Parks_NothingSent_NoWaits()
    {
        // DELIBERATE PIN REVISION (council 5-1, probe-gated 2026-07-28,
        // supersedes the paste-path-hardening fail-open pin): a 0 foreground
        // hwnd at send start means the foreground is unobservable at exactly
        // the moment we are about to type -- blind-injecting can silently
        // lose the whole text, while a park is a visible one-click detour.
        // Probe evidence: 0-readings never occur at rest; they occur only in
        // 0.3-3.7 ms bursts during focus transitions. The park must land
        // BEFORE the modifier/mouse release-wait preludes (no sleeps) and
        // before any send.
        var sent = new List<string>();
        var sleeps = new List<int>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => true,           // everything held: proves no prelude ran
            foregroundHwnd: () => 0,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: sleeps.Add);

        injector.TryInjectGuarded(new string('a', 80))
            .ShouldBe(InjectionRunOutcome.NoForeground);

        sent.ShouldBeEmpty();
        sleeps.ShouldBeEmpty();
    }

    [Fact]
    public void Guarded_ZeroForegroundAtStart_CountsAtStartOccurrences()
    {
        var injector = NewInjector(() => 0, c => true);

        injector.TryInjectGuarded("abc");
        injector.TryInjectGuarded("def");

        injector.Meter.AtStartCount.ShouldBe(2);
        injector.Meter.MidStreamCount.ShouldBe(0);
    }

    [Fact]
    public void Guarded_ProbeGoesToZero_MidSend_Interrupts_AndCountsMidStream()
    {
        var sent = new List<string>();
        var probes = 0;
        var injector = NewInjector(
            () => ++probes == 1 ? 42L : 0L,   // start check sees 42; per-chunk sees 0
            c => { sent.Add(c); return true; });
        var text = new string('a', 24);       // 3 chunks of 8

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Interrupted);

        string.Concat(sent).Length.ShouldBeLessThan(text.Length);
        injector.Meter.MidStreamCount.ShouldBe(1);
        injector.Meter.AtStartCount.ShouldBe(0);
    }

    [Fact]
    public void Guarded_DefaultForegroundProbe_OffWindows_ParksAtStart()
    {
        // Off-Windows-only pin: on the Windows gate's interactive desktop the
        // real DefaultForegroundProbe returns a live nonzero hwnd, so the run
        // would proceed (Completed / BlockedElevated) and the assertion below
        // would fail. Same guard pattern as
        // ElevationProbeTests.Probe_OffWindows_ReturnsUnknown_FailOpen.
        if (OperatingSystem.IsWindows()) return;

        // The production default probe returns 0 unconditionally off-Windows
        // (TextInjector.DefaultForegroundProbe). Under the new fail-safe
        // polarity an unseamed injector therefore PARKS off-Windows instead
        // of injecting blind -- pinned deliberately so the off-Windows
        // default flip is a documented decision, not an accident. Production
        // is Windows-only; every Linux test that wants a send seams
        // foregroundHwnd explicitly.
        var sent = new List<string>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: _ => { });

        injector.TryInjectGuarded("abc").ShouldBe(InjectionRunOutcome.NoForeground);
        sent.ShouldBeEmpty();
    }

    [Fact]
    public void Guarded_ModifierPressed_MidSend_Interrupts()
    {
        // The halt gesture's LEADING edge is a physical modifier going down
        // (Alt, before Alt+Tab moves the foreground). The guard halts on the
        // modifier itself so no chunk goes out Alt-modified (ledger A6).
        var sent = new List<string>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => sent.Count >= 1, // "Alt goes down" after chunk 1
            foregroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: _ => { });
        var text = new string('a', 96); // 12 chunks of 8

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Interrupted);

        sent.Count.ShouldBe(1);
    }

    [Fact]
    public void Guarded_Paces_Between_Chunks_Only()
    {
        // SEMANTIC CHANGE (deadline pacing, 2026-07-28): the values recorded
        // through the sleep seam are now the requested REMAINDERS of each
        // 14 ms period, not fixed requested pauses. With a frozen monotonic
        // clock the measured elapsed is 0, so every remainder is the full
        // InterChunkPauseMs -- which keeps this the canonical
        // "pauses run between chunks only" pin.
        var sleeps = new List<int>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: _ => true,
            sleep: sleeps.Add,
            monotonicMs: () => 0.0);
        var text = new string('a', 96); // 12 chunks => exactly 11 inter-chunk pauses

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        sleeps.ShouldBe(new[] { 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14 }); // 11 x TextInjector.InterChunkPauseMs remainders (frozen clock => elapsed 0)
    }

    [Fact]
    public void TryInject_Adapter_True_OnCompleted_False_OnInterrupted()
    {
        var stable = NewInjector(() => 42, _ => true);
        stable.TryInject("hi").ShouldBeTrue();

        var probes = 0;
        var moving = NewInjector(() => ++probes == 1 ? 42L : 99L, _ => true);
        moving.TryInject("hi").ShouldBeFalse();
    }

    [Fact]
    public void DesignPoint_FeedRateCeiling_And_BleedBound()
    {
        // Spec constraint (paste-path-hardening, 2026-07-27): the nominal
        // feed rate must stay AT OR BELOW TargetFeedUnitsPerSecond so the
        // queued-but-undelivered backlog cannot grow against slow-rendering
        // apps (~600 chars/s claimed render rate) -- a mid-paste window
        // switch then leaks at most the true in-flight chunk. The pause is
        // CEILING-derived for exactly this reason: truncating division gave
        // 13 ms = ~615 units/s, ABOVE the target (stage-2 ledger A1). This
        // DELIBERATELY SUPERSEDES the bleed-hardening plan's ">= 1600"
        // floor (owner-approved). The feed must not collapse either, and
        // the worst-case bleed bound (<= 1 in-flight chunk, prior ledger
        // AD-1) must not regress past 8 code units.
        var nominalFeed = TextInjector.ChunkCodeUnits * 1000 / TextInjector.InterChunkPauseMs; // 571 at 8/14ms
        nominalFeed.ShouldBeLessThanOrEqualTo(TextInjector.TargetFeedUnitsPerSecond); // never exceed the render-rate target
        nominalFeed.ShouldBeGreaterThanOrEqualTo(500); // sanity floor: still responsive in fast apps
        TextInjector.ChunkCodeUnits.ShouldBeLessThanOrEqualTo(8);
    }

    [Fact]
    public void Guarded_ModifierWait_UsesInjectedSleep_NeverWallClock()
    {
        // The modifier-release prelude must poll through the injected sleep
        // seam (_sleep), not Thread.Sleep -- virtual-time tests depend on it.
        var sleeps = new List<int>();
        var held = true;
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: vk => vk == 0x11 && held, // Ctrl held...
            foregroundHwnd: () => 42,
            sendChunk: _ => true,
            sleep: ms =>
            {
                sleeps.Add(ms);
                if (sleeps.Count >= 2) held = false; // ...released after 2 polls
            });

        // "hi" = 1 chunk => no inter-chunk pauses; the only sleeps are the
        // two 15 ms modifier-wait polls, recorded through the seam.
        injector.TryInjectGuarded("hi").ShouldBe(InjectionRunOutcome.Completed);

        sleeps.ShouldBe(new[] { 15, 15 });
    }

    [Fact]
    public void Guarded_PillClick_ButtonStillDownAtStart_WaitsForRelease_ThenSendsAll()
    {
        // THE pill-click regression pin: TryPastePending runs inside the
        // pill's PointerPressed handler (button-DOWN edge) on the UI thread,
        // so VK_LBUTTON is still physically down when injection starts. The
        // guard must WAIT for the release (GetAsyncKeyState reads physical
        // state -- observable even though the blocked UI thread never pumps
        // the pointer-up message), then paste ALL the text. It must never
        // self-cancel on the click that requested the paste.
        var sleeps = new List<int>();
        var buttonDown = true;
        var sent = new List<string>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: vk => vk == 0x01 && buttonDown,
            foregroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: ms => { sleeps.Add(ms); if (sleeps.Count >= 3) buttonDown = false; },
            monotonicMs: () => 0.0);
        var text = new string('a', 16); // 2 chunks of 8

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        string.Concat(sent).ShouldBe(text);
        // Three 15 ms release-wait polls, then the single inter-chunk REMAINDER (frozen clock => full 14 ms).
        sleeps.ShouldBe(new[] { 15, 15, 15, 14 });
    }

    [Fact]
    public void Guarded_ButtonHeldPastTimeout_Interrupts_SendsNothing()
    {
        // No safe neutralization exists for a mouse button (a synthesized
        // button-up would fabricate a click), so a button held past the
        // bounded wait ABORTS the run: the caller keeps the FULL text
        // pending; nothing is sprayed under the still-held pointer.
        var sleeps = new List<int>();
        var sent = new List<string>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: vk => vk == 0x01, // left button held forever
            foregroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: sleeps.Add);

        injector.TryInjectGuarded("hello").ShouldBe(InjectionRunOutcome.Interrupted);

        sent.ShouldBeEmpty();
        sleeps.Sum().ShouldBe(1500); // waited the full bounded budget, no longer
    }

    [Fact]
    public void Guarded_MouseButtonPressed_MidSend_Interrupts_AfterPrefixOnly()
    {
        // Click-to-switch: the button-down is the leading edge of the focus
        // change, observable BEFORE the foreground flips (Alt+Tab's modifier
        // analogue). Halt on it; the caller parks the full text.
        var sent = new List<string>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: vk => vk == 0x01 && sent.Count >= 1, // click after chunk 1
            foregroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: _ => { });
        var text = new string('a', 24); // 3 chunks of 8

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Interrupted);

        sent.Count.ShouldBe(1);
    }

    [Fact]
    public void Guarded_FocusChange_DuringMouseWait_SendsNothing()
    {
        // The HWND baseline is captured at method ENTRY, before both
        // preludes: if the user's click lands in another window while we
        // wait for the button release, the pre-chunk-0 check catches it and
        // nothing is typed into the new window.
        var sent = new List<string>();
        var probes = 0;
        var buttonDown = true;
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: vk => vk == 0x01 && buttonDown,
            foregroundHwnd: () => ++probes == 1 ? 42L : 99L,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: _ => buttonDown = false); // released after the first poll

        injector.TryInjectGuarded("hello").ShouldBe(InjectionRunOutcome.Interrupted);

        sent.ShouldBeEmpty();
    }

    [Fact]
    public void Guarded_MouseProbeUnavailable_FailOpen_SendsEverything()
    {
        // A probe that cannot observe reports "up" (GetAsyncKeyState has no
        // error channel; non-Windows returns false). We never hold a paste
        // because we merely failed to observe.
        var sent = new List<string>();
        var injector = NewInjector(() => 42, c => { sent.Add(c); return true; });
        var text = new string('a', 80);

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        string.Concat(sent).ShouldBe(text);
    }

    [Fact]
    public void Guarded_ElevatedForeground_BlocksBeforeAnyKeystrokeOrWait()
    {
        // UIPI pre-check (paste-path-hardening): SendInput into an elevated
        // window is silently dropped while reporting success, so the run
        // must not start at all. The block must land BEFORE the modifier
        // and mouse release-wait preludes (no sleeps) and before any send.
        // isKeyDown reports everything held to prove no prelude ran.
        var sent = new List<string>();
        var sleeps = new List<int>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => true,
            foregroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: sleeps.Add,
            foregroundElevation: _ => ForegroundElevation.Elevated);

        injector.TryInjectGuarded("text for an admin window")
            .ShouldBe(InjectionRunOutcome.BlockedElevated);

        sent.ShouldBeEmpty();
        sleeps.ShouldBeEmpty(); // blocked before the release-wait preludes
    }

    [Fact]
    public void Guarded_ElevationUnknown_FailsOpen_AndSendsAll()
    {
        // Transient observation failure => today's behavior, unchanged.
        var sent = new List<string>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: _ => { },
            foregroundElevation: _ => ForegroundElevation.Unknown);
        var text = new string('a', 16); // 2 chunks of 8

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        string.Concat(sent).ShouldBe(text);
    }

    [Fact]
    public void Guarded_ElevationNotElevated_SendsAll()
    {
        var sent = new List<string>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: _ => { },
            foregroundElevation: _ => ForegroundElevation.NotElevated);
        var text = new string('a', 16);

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        string.Concat(sent).ShouldBe(text);
    }

    [Fact]
    public void Guarded_DefaultElevationProbe_OffWindows_FailsOpen()
    {
        // Construct WITHOUT the elevation seam: the production default
        // (ElevationProbe.Probe) must fail open off-Windows so every
        // existing Linux test and non-Windows path is unaffected.
        var sent = new List<string>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: _ => { });
        var text = new string('a', 8);

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        string.Concat(sent).ShouldBe(text);
    }

    [Fact]
    public void Guarded_DeadlinePacing_SleepsOnlyTheRemainderOfThePeriod()
    {
        // Deadline pacing (2026-07-28): the pause accounts for the send
        // call's own measured duration. A 5 ms send inside a 14 ms period
        // leaves a 9 ms remainder.
        var sleeps = new List<int>();
        var now = 0.0;
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: _ => { now += 5.0; return true; }, // each send "costs" 5 ms
            sleep: ms => { sleeps.Add(ms); now += ms; },
            monotonicMs: () => now);
        var text = new string('a', 24); // 3 chunks => 2 inter-chunk pauses

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        sleeps.ShouldBe(new[] { 9, 9 }); // 14 - 5 measured send ms, per period
    }

    [Fact]
    public void Guarded_SendSlowerThanPeriod_SleepsZeroTimes_GuardCadenceUnchanged()
    {
        // When SendInput alone takes >= the 14 ms period, the injector must
        // not add ANY sleep (the send itself throttles the feed at or below
        // the safe rate) -- and the halt-predicate cadence must not change:
        // still exactly one foreground check per chunk plus the baseline.
        var sleeps = new List<int>();
        var now = 0.0;
        var hwndReads = 0;
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => { hwndReads++; return 42; },
            sendChunk: _ => { now += 20.0; return true; }, // send alone exceeds the period
            sleep: ms => { sleeps.Add(ms); now += ms; },
            monotonicMs: () => now);
        var text = new string('a', 24); // 3 chunks

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        sleeps.ShouldBeEmpty();
        hwndReads.ShouldBe(4); // 1 baseline at entry + 1 mid-paste check per chunk
    }

    [Fact]
    public void Guarded_NineUnitStraddleChunks_GetTheScaledPeriodRemainder()
    {
        // InjectionChunker extends a chunk to 9 units rather than split a
        // surrogate pair. A fixed 14 ms period would let sustained 9-unit
        // chunks feed ~643 units/s > 600, so the period scales per chunk:
        // ceil(9 * 14 / 8) = 16 ms (stage-2 ledger A7). Frozen clock =>
        // the full scaled period is requested through the sleep seam.
        var sleeps = new List<int>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: _ => true,
            sleep: sleeps.Add,
            monotonicMs: () => 0.0);
        var block = "a\U0001F600\U0001F600\U0001F600\U0001F600"; // 9 units: 1 BMP char + 4 surrogate pairs
        var text = block + block + block; // 3 straddle chunks of 9 units => 2 inter-chunk pauses

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        sleeps.ShouldBe(new[] { 16, 16 }); // TextInjector.PeriodMsForChunk(9-unit chunk) remainders
    }
}
