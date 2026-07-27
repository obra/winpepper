using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class GuardedInjectionRunTests
{
    [Fact]
    public void StableFocus_SendsAllChunks_Completed()
    {
        var sent = new List<string>();
        var outcome = GuardedInjectionRun.Execute(
            chunks: new[] { "aa", "bb", "cc" },
            hwndAtSendStart: 42,
            currentForegroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; });

        outcome.ShouldBe(InjectionRunOutcome.Completed);
        sent.ShouldBe(new[] { "aa", "bb", "cc" });
    }

    [Fact]
    public void FocusChange_BeforeFirstChunk_Interrupts_WithNothingSent()
    {
        // Focus can move during the pre-send modifier-release wait (up to
        // 1500 ms) -- the guard must catch that before the FIRST chunk.
        var sent = new List<string>();
        var outcome = GuardedInjectionRun.Execute(
            chunks: new[] { "aa", "bb" },
            hwndAtSendStart: 42,
            currentForegroundHwnd: () => 99,
            sendChunk: c => { sent.Add(c); return true; });

        outcome.ShouldBe(InjectionRunOutcome.Interrupted);
        sent.ShouldBeEmpty();
    }

    [Fact]
    public void FocusChange_MidRun_Interrupts_AfterPrefixOnly()
    {
        var sent = new List<string>();
        var probes = 0;
        var outcome = GuardedInjectionRun.Execute(
            chunks: new[] { "aa", "bb", "cc" },
            hwndAtSendStart: 42,
            // First probe (before chunk 1) sees the original window; every
            // later probe sees a different one.
            currentForegroundHwnd: () => ++probes == 1 ? 42L : 99L,
            sendChunk: c => { sent.Add(c); return true; });

        outcome.ShouldBe(InjectionRunOutcome.Interrupted);
        sent.ShouldBe(new[] { "aa" });
    }

    [Fact]
    public void SendFailure_ReturnsSendFailed_AndStops()
    {
        var sent = new List<string>();
        var outcome = GuardedInjectionRun.Execute(
            chunks: new[] { "aa", "bb", "cc" },
            hwndAtSendStart: 42,
            currentForegroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return sent.Count < 2; });

        outcome.ShouldBe(InjectionRunOutcome.SendFailed);
        sent.ShouldBe(new[] { "aa", "bb" });
    }

    [Fact]
    public void EmptyChunks_Completed_WithoutProbing()
    {
        var outcome = GuardedInjectionRun.Execute(
            chunks: Array.Empty<string>(),
            hwndAtSendStart: 42,
            currentForegroundHwnd: () => throw new InvalidOperationException("must not probe"),
            sendChunk: _ => throw new InvalidOperationException("must not send"));

        outcome.ShouldBe(InjectionRunOutcome.Completed);
    }

    [Fact]
    public void FailOpen_UnknownBaseline_SendsEverything()
    {
        // hwndAtSendStart == 0 => guard disabled; behaves exactly like the
        // old unguarded send even though the probe reports a different hwnd.
        var sent = new List<string>();
        var outcome = GuardedInjectionRun.Execute(
            chunks: new[] { "aa", "bb" },
            hwndAtSendStart: 0,
            currentForegroundHwnd: () => 99,
            sendChunk: c => { sent.Add(c); return true; });

        outcome.ShouldBe(InjectionRunOutcome.Completed);
        sent.Count.ShouldBe(2);
    }

    [Fact]
    public void Interrupted_Run_Sent_Text_Is_A_Strict_Prefix_Never_The_Whole()
    {
        // The user story: on interrupt the target got only a leading prefix.
        // The CALLER is then required to hold the WHOLE original text as the
        // pending paste (PipelineHost passes `final`, not the remainder) --
        // this test pins the "strict prefix" half of that contract.
        var text = new string('x', 100);
        var chunks = InjectionChunker.Split(text, 32);
        var sent = new List<string>();
        var probes = 0;
        var outcome = GuardedInjectionRun.Execute(
            chunks,
            hwndAtSendStart: 42,
            currentForegroundHwnd: () => ++probes <= 2 ? 42L : 99L,
            sendChunk: c => { sent.Add(c); return true; });

        outcome.ShouldBe(InjectionRunOutcome.Interrupted);
        var sentText = string.Concat(sent);
        sentText.Length.ShouldBeLessThan(text.Length);
        text.StartsWith(sentText, StringComparison.Ordinal).ShouldBeTrue();
    }

    [Fact]
    public void ModifierDown_MidRun_Interrupts_AfterPrefixOnly()
    {
        // The halt gesture's LEADING edge is a physical modifier going down
        // (Alt, before Alt+Tab moves the foreground). The guard must halt on
        // the modifier itself so no chunk goes out Alt-modified (ledger A6).
        var sent = new List<string>();
        var outcome = GuardedInjectionRun.Execute(
            chunks: new[] { "aa", "bb", "cc" },
            hwndAtSendStart: 42,
            currentForegroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; },
            physicalInputDown: () => sent.Count >= 1); // "Alt goes down" after chunk 1

        outcome.ShouldBe(InjectionRunOutcome.Interrupted);
        sent.ShouldBe(new[] { "aa" });
    }

    [Fact]
    public void Pause_Runs_Between_Chunks_Never_Before_The_First()
    {
        var events = new List<string>();
        var outcome = GuardedInjectionRun.Execute(
            chunks: new[] { "aa", "bb", "cc" },
            hwndAtSendStart: 42,
            currentForegroundHwnd: () => 42,
            sendChunk: c => { events.Add("send:" + c); return true; },
            pauseBetweenChunks: () => events.Add("pause"));

        outcome.ShouldBe(InjectionRunOutcome.Completed);
        events.ShouldBe(new[] { "send:aa", "pause", "send:bb", "pause", "send:cc" });
    }
}
