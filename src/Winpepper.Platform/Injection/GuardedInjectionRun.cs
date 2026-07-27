using System;
using System.Collections.Generic;

namespace Winpepper.Platform.Injection;

/// <summary>
/// Pure driver for an interruptible, chunked, PACED injection send. The pause
/// runs BETWEEN chunks (never before the first): without pacing the whole
/// loop completes in single-digit milliseconds (SendInput is queue-insertion,
/// ~µs per call) and no human focus change could ever be observed mid-run.
/// Before EVERY chunk (including the first -- the modifier-release wait can
/// delay the first keystroke by up to 1500 ms) it checks, in order: has a
/// physical modifier gone down (the leading edge of a halt gesture -- Alt is
/// down before Alt+Tab changes the foreground), then asks
/// <see cref="MidPasteDecider"/> whether the window we started typing into is
/// still foreground. On either halt it stops immediately and reports
/// <see cref="InjectionRunOutcome.Interrupted"/> so the caller can hold the
/// WHOLE original text as a pending paste. All Win32 access is behind the
/// delegates, so this loop is fully unit-testable on Linux.
/// </summary>
public static class GuardedInjectionRun
{
    public static InjectionRunOutcome Execute(
        IReadOnlyList<string> chunks,
        long hwndAtSendStart,
        Func<long> currentForegroundHwnd,
        Func<string, bool> sendChunk,
        Func<bool>? modifierHeld = null,
        Action? pauseBetweenChunks = null)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(currentForegroundHwnd);
        ArgumentNullException.ThrowIfNull(sendChunk);

        for (var i = 0; i < chunks.Count; i++)
        {
            if (i > 0) pauseBetweenChunks?.Invoke();

            // Checks sit immediately before the send, so the exposure window
            // is the (microsecond-scale) send itself, not the pause.
            if (modifierHeld?.Invoke() == true)
                return InjectionRunOutcome.Interrupted;
            if (MidPasteDecider.Decide(hwndAtSendStart, currentForegroundHwnd())
                == MidPasteDecision.Halt)
            {
                return InjectionRunOutcome.Interrupted;
            }
            if (!sendChunk(chunks[i]))
                return InjectionRunOutcome.SendFailed;
        }
        return InjectionRunOutcome.Completed;
    }
}
