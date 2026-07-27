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
    public void Guarded_UnknownBaseline_FailOpen_SendsEverything()
    {
        // Probe returns 0 (non-Windows / GetForegroundWindow failed): the
        // guard is disabled and the paste behaves exactly like today.
        var sent = new List<string>();
        var injector = NewInjector(() => 0, c => { sent.Add(c); return true; });
        var text = new string('a', 80);

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        string.Concat(sent).ShouldBe(text);
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
        var sleeps = new List<int>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: _ => true,
            sleep: sleeps.Add);
        var text = new string('a', 96); // 12 chunks => exactly 11 inter-chunk pauses

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        sleeps.ShouldBe(new[] { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 }); // 11 x TextInjector.InterChunkPauseMs
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
    public void DesignPoint_FeedRateFloor_And_BleedBound()
    {
        // Spec constraint: the effective feed rate must never drop below the
        // original 1600 code units/s design point, and the worst-case bleed
        // into a newly focused window (<= 1 in-flight chunk, prior ledger
        // AD-1, hardened by this task) must not regress past 8 code units.
        (TextInjector.ChunkCodeUnits * 1000 / TextInjector.InterChunkPauseMs)
            .ShouldBeGreaterThanOrEqualTo(1600);
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
            sleep: ms => { sleeps.Add(ms); if (sleeps.Count >= 3) buttonDown = false; });
        var text = new string('a', 16); // 2 chunks of 8

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        string.Concat(sent).ShouldBe(text);
        // Three 15 ms release-wait polls, then the single 5 ms inter-chunk pause.
        sleeps.ShouldBe(new[] { 15, 15, 15, 5 });
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
}
