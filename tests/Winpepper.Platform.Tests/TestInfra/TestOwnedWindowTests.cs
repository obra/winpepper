#if WINDOWS
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.TestInfra;

/// <summary>
/// Acceptance guard for the test-owned window: the REAL production read
/// (UiaTreeReader + UiaTreeOrdering.Compose, exactly the composition
/// WindowContextPrefetch.CreateWindows uses) against the owned window must
/// recover the sentinel text, above the 80-char viability floor, fast. This
/// is the evidence that the determinism mechanism itself works (off-screen,
/// never activated, real UIA); the regime/contention facts that consume it
/// then stop depending on ambient foreground focus and host load.
/// </summary>
[Trait("Platform", "Windows")]
public class TestOwnedWindowTests
{
    private readonly ITestOutputHelper _log;
    public TestOwnedWindowTests(ITestOutputHelper log) => _log = log;

    [Fact]
    public async Task OwnedWindow_RealUiaRead_YieldsSentinelText_WithinBudget()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var window = TestOwnedWindow.Create();
        Assert.SkipUnless(window is not null,
            "could not create the test-owned window in this session");

        var reader = new UiaTreeReader(NullLogger<UiaTreeReader>.Instance);

        // The FIRST real UIA call in a fresh process pays the UIAutomationCore /
        // theme bootstrap once (observed 137-2244ms cold vs 2-13ms warm on the
        // gate host at 100% cpu; the regime facts absorb that one-off via their
        // head-start + bounded retries). This fact guards the STEADY-STATE
        // determinism the regime facts rely on, so the cold read is paid here
        // first and only later reads are timed.
        _ = await ReadOnceAsync(window.Hwnd);

        // Steady-state guard. Observed flake modes during development: a read
        // returning 0 elements in ~2ms (1-in-8 single runs) and, once, three such
        // reads in a row inside the parallel full-assembly gate context. Retrying
        // against a FRESH window each attempt keeps the guard honest — a genuinely
        // broken extraction (regression in the machinery itself) fails every read
        // of every window, while environment-shaped window-creation/probe races
        // get absorbed without weakening the sentinel/length/timing assertions.
        string? text = null;
        var sw = Stopwatch.StartNew();
        TestOwnedWindow? attemptWindow = null;
        try
        {
            for (var attempt = 1; attempt <= 3 && text is null; attempt++)
            {
                attemptWindow = TestOwnedWindow.Create();
                Assert.SkipUnless(attemptWindow is not null,
                    "could not create the test-owned window in this session");
                sw.Restart();
                var elements = await ReadOnceAsync(attemptWindow.Hwnd);
                sw.Stop();
                text = UiaTreeOrdering.Compose(elements);
                _log.WriteLine(
                    $"owned-window read attempt {attempt}: elapsed={sw.ElapsedMilliseconds}ms elements={elements.Count} composed={(text is null ? "null" : $"{text.Length} chars")}");
                if (elements.Count == 0)
                    _log.WriteLine("  zero-element manual probe: " + ManualProbe(attemptWindow.Hwnd));
                sw.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(1500),
                    $"\nreading the test-owned 3-node window took {sw.ElapsedMilliseconds}ms — the determinism window the gate facts rely on is broken on this host");
                if (text is null) { attemptWindow.Dispose(); attemptWindow = null; }
            }
        }
        finally
        {
            attemptWindow?.Dispose();
        }

        text.ShouldNotBeNull("the owned window's real UIA read produced no viable text");
        text!.Length.ShouldBeGreaterThanOrEqualTo(UiaTreeOrdering.DefaultMinViableChars);
        text.ShouldContain(TestOwnedWindow.SentinelText);
    }

    private static Task<List<UiaExtractedElement>> ReadOnceAsync(IntPtr hwnd) =>
        Task.Run(() => new UiaTreeReader(NullLogger<UiaTreeReader>.Instance)
            .ReadForeground(hwnd, CancellationToken.None));

    // Diagnostic (kept deliberately): mirrors ReadForeground's FromHandle+walk
    // steps directly so a hidden catch inside the production reader (LogDebug
    // against NullLogger when FromHandle fails) can be pinpointed from the gate
    // log if the zero-element flake ever reappears. Runs only on that failure
    // path; returns the annotated description.
    private static string ManualProbe(IntPtr hwnd)
    {
        System.Windows.Automation.AutomationElement? root;
        try
        {
            root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            if (root is null) return "FromHandle-null";
        }
        catch (System.Exception ex) { return $"FromHandle threw {ex.GetType().Name}: {ex.Message}"; }

        var visited = new System.Collections.Generic.List<string>();
        var stack = new System.Collections.Generic.Stack<System.Windows.Automation.AutomationElement>();
        stack.Push(root);
        var n = 0;
        while (stack.Count > 0 && n++ < 20)
        {
            var cur = stack.Pop();
            try { visited.Add($"{cur.Current.ClassName}/{cur.Current.ControlType.ProgrammaticName}/nameLen={(cur.Current.Name ?? "").Length}"); }
            catch (System.Exception ex) { visited.Add($"<props threw {ex.GetType().Name}>"); }
            try
            {
                var child = System.Windows.Automation.TreeWalker.ContentViewWalker.GetFirstChild(cur);
                while (child != null) { stack.Push(child); child = System.Windows.Automation.TreeWalker.ContentViewWalker.GetNextSibling(child); }
            }
            catch (System.Exception ex) { visited.Add($"<walk threw {ex.GetType().Name}>"); }
        }
        return $"visited={visited.Count}: " + string.Join(" | ", visited);
    }
}
#endif
