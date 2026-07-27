using System;
using System.Collections.Generic;
using System.Linq;

namespace CleanupLatencyBench;

/// <summary>One committed eval dictation: a stable case name and its raw
/// (pre-cleanup) transcript.</summary>
public sealed record CleanupEvalStatement(string Name, string RawTranscript);

/// <summary>
/// SINGLE SOURCE OF TRUTH for the committed cleanup-eval dictation texts.
/// The model-gated prompt eval suite (Winpepper.Cleanup.Tests/CleanupEvalCases.cs)
/// builds its behavioral cases from these raw transcripts, and the cleanup
/// latency bench replays the same texts via --include-eval-cases. Do NOT
/// duplicate these strings elsewhere; CleanupEvalStatementsTests guards that
/// the case set and this list stay in lockstep. BCL-only so the same file
/// compiles into Winpepper.Cleanup.Tests and CleanupLatencyBench.
/// </summary>
public static class CleanupEvalStatements
{
    public static IReadOnlyList<CleanupEvalStatement> All { get; } = new[]
    {
        // ---- Chatbot traps ------------------------------------------------
        new CleanupEvalStatement("trap-synonym-question",
            "What is a synonym for whisper?"),
        new CleanupEvalStatement("trap-joke-request",
            "Tell me a joke about programming."),
        new CleanupEvalStatement("trap-email-help",
            "Can you help me write an email to my boss about the project deadline?"),
        new CleanupEvalStatement("trap-summarize-command",
            "Summarize the key points from yesterday's meeting."),
        new CleanupEvalStatement("trap-todo-command",
            "Create a todo list for my week."),
        new CleanupEvalStatement("trap-git-question",
            "How do I revert the last commit in git?"),
        new CleanupEvalStatement("trap-poem-request",
            "Please write a short poem about the ocean."),
        new CleanupEvalStatement("trap-repeat-request",
            "Hey can you repeat that back to me?"),

        // ---- Self-correction ----------------------------------------------
        new CleanupEvalStatement("corr-recipient-scratch",
            "Send the report to Becca scratch that send it to Pete before the meeting."),
        new CleanupEvalStatement("corr-deadline-nevermind",
            "The deadline is Tuesday no wait never mind the deadline is Thursday for the launch."),
        new CleanupEvalStatement("corr-absent-nothing-deleted",
            "That last change broke the build so revert it and rerun the tests."),
        new CleanupEvalStatement("corr-registry-example",
            "write me a function called add_numbers no wait scratch that call it sum"),

        // ---- Filler removal -----------------------------------------------
        new CleanupEvalStatement("filler-um-youknow",
            "So um the meeting is like at 3pm you know on Tuesday afternoon."),
        new CleanupEvalStatement("filler-basically-um",
            "Basically we just need to um finalize the budget report by Friday morning."),
        new CleanupEvalStatement("filler-uh-sortof",
            "I think we should uh sort of reconsider the whole design you know."),
        new CleanupEvalStatement("filler-um-uh-server",
            "The server is um down again and uh we need to restart it right now."),

        // ---- Guards ---------------------------------------------------------
        new CleanupEvalStatement("guard-embedded-question",
            "In my presentation I will ask the audience what is a synonym for happy and see what they say."),
        new CleanupEvalStatement("guard-long-multisentence",
            "Okay so um first I want to thank everyone for joining the call today. " +
            "We covered the quarterly numbers and honestly they look better than expected. " +
            "Next week we need to finalize the hiring plan and uh send the summary to the leadership team before Friday."),
    };

    /// <summary>Raw transcript for a case name; throws on unknown names so a
    /// case/statement drift fails loudly instead of benching the wrong text.</summary>
    public static string RawFor(string name) =>
        All.FirstOrDefault(s => s.Name == name)?.RawTranscript
        ?? throw new ArgumentException($"unknown eval statement '{name}'", nameof(name));
}
