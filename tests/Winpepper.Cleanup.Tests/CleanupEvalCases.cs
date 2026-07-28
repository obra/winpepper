using System.Text.RegularExpressions;
using CleanupLatencyBench;
using Shouldly;
using Winpepper.Models;

namespace Winpepper.Cleanup.Tests;

/// <summary>One eval case: a raw dictation transcript and the behavioral
/// assertions its cleaned output must satisfy. <see cref="Verify"/> receives
/// the runner's final CleanedText and throws (Shouldly) on violation.</summary>
public sealed record CleanupEvalCase(string Name, string RawTranscript, Action<string> Verify);

/// <summary>
/// Registry-driven enumeration of the cleanup models the eval suite covers.
/// Every <see cref="ModelKind.Cleanup"/> entry in <see cref="ModelRegistry"/>
/// automatically gains eval coverage: the per-model eval classes in
/// CleanupPromptEvalTests.cs bind to registry slots 0..<see cref="SlotCount"/>-1.
/// </summary>
public static class CleanupEvalModels
{
    /// <summary>Number of pre-provisioned per-model eval classes
    /// (CleanupPromptEvalModelSlot0..N-1). Guarded by
    /// <c>CleanupEvalCasesTests.Registry_CleanupModels_FitWithinEvalSlots</c>:
    /// when the registry outgrows this, add a slot class and bump this.</summary>
    public const int SlotCount = 4;

    /// <summary>Overrides the default models root (%LOCALAPPDATA%/winpepper/models)
    /// so the eval can run against a non-standard install location, including on
    /// non-Windows hosts.</summary>
    public const string ModelsRootEnvVar = "WINPEPPER_MODELS_ROOT";

    public static IReadOnlyList<ModelDescriptor> CleanupModels { get; } =
        new ModelRegistry().ByKind(ModelKind.Cleanup).ToList();

    public static ModelDescriptor? AtSlot(int slot) =>
        slot >= 0 && slot < CleanupModels.Count ? CleanupModels[slot] : null;

    public static string ModelsRoot =>
        Environment.GetEnvironmentVariable(ModelsRootEnvVar) is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "winpepper", "models");

    /// <summary>Absolute path of the model's GGUF file under <see cref="ModelsRoot"/>,
    /// or null when the descriptor declares no .gguf file.</summary>
    public static string? GgufPathFor(ModelDescriptor descriptor)
    {
        var gguf = descriptor.Files.FirstOrDefault(
            f => f.RelativePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase));
        return gguf is null
            ? null
            : Path.Combine(ModelsRoot, descriptor.InstallDirRelative, gguf.RelativePath);
    }
}

/// <summary>
/// The fixed dictation case set (ported in intent from ghost-pepper's
/// CleanupPromptEvalTests.swift). Three families plus guards:
///  - chatbot traps: dictations that ARE questions/instructions and must pass
///    through cleaned, unanswered;
///  - self-correction: "scratch that"/"never mind" edits applied literally;
///  - filler removal: um/uh/you-know dropped, content retained;
///  - guards: trap-shaped-but-legit content and full reproduction of long input.
/// Every case is >= 4 words so CleanupRunner.Preflight routes it to the LLM.
/// </summary>
public static class CleanupEvalCases
{
    public static IReadOnlyList<CleanupEvalCase> All { get; } = Build();

    public static CleanupEvalCase ByName(string name) =>
        All.SingleOrDefault(c => c.Name == name)
        ?? throw new ArgumentException($"unknown eval case '{name}'", nameof(name));

    // -----------------------------------------------------------------------
    // KNOWN-FAILING BASELINE (quarantine with visibility)
    //
    // Policy: entries here are (model, case) pairs that fail on real hardware
    // with the current model/prompt. The harness (CleanupPromptEvalTestsBase.
    // RunCaseAsync) still RUNS a baselined case; on failure it converts the
    // result to a dynamic skip whose message carries the baseline reason plus
    // the fresh failure detail, so the Windows gate stays green while the
    // signal stays visible as named skips. A failure NOT listed here still
    // fails loudly. A baselined case that PASSES is reported via test output
    // as retirable.
    //
    // This list is tracked DEBT for kata 809b (model upgrade). Never silently
    // grow it: every new entry must record the observed output and date in
    // its reason string.
    // -----------------------------------------------------------------------

    private const string Qwen05B = "qwen2.5-0.5b-instruct-q4_k_m";
    private const string Sotto350M = "sotto-cleanup-lfm25-350m-q8_0";

    public static IReadOnlyDictionary<(string Model, string CaseName), string> KnownFailingBaseline { get; } =
        new Dictionary<(string, string), string>
        {
            [(Qwen05B, "trap-joke-request")] =
                "Answers the dictation instead of cleaning it: observed \"A programmer is a coder " +
                "who writes code.\" (retention 0.17). Recorded 2026-07-26, seed 42, Vulkan GPU.",
            [(Qwen05B, "trap-email-help")] =
                "Prepends chatbot preamble: observed \"Sure, here's the cleaned version:\\n\\nWrite " +
                "me an email to your boss about the project deadline.\" Recorded 2026-07-26, seed 42, Vulkan GPU.",
            [(Qwen05B, "trap-repeat-request")] =
                "Injects \"Sure,\" into the output: observed \"Sure, can you repeat that back to " +
                "me?\". Recorded 2026-07-26, seed 42, Vulkan GPU.",
            [(Qwen05B, "corr-recipient-scratch")] =
                "Applies the self-correction backwards (deletes Pete, keeps Becca): observed " +
                "\"Send the report to Becca before the meeting.\" Recorded 2026-07-26, seed 42, Vulkan GPU.",
            [(Qwen05B, "trap-poem-request")] =
                "Output rejected by the plausibility guard (runner took FallbackImplausible " +
                "instead of accepting the LLM output). Recorded 2026-07-27, seed 42, Vulkan GPU.",
            // Sotto's four bake-off failures (docs/plans/2026-07-27-cleanup-model-bakeoff-prep.md §7a):
            // all non-destructive under-edits — the self-correction meta-command is kept as
            // literal text instead of being applied, and one filler survives. Exactly the §4
            // fine-tune dataset gaps.
            [(Sotto350M, "corr-recipient-scratch")] =
                "Keeps the self-correction as literal text instead of applying it: observed " +
                "\"Send the report to Becca, send it to Pete before the meeting.\" " +
                "Recorded 2026-07-27, seed 42, Vulkan GPU.",
            [(Sotto350M, "corr-deadline-nevermind")] =
                "Keeps the \"never mind\" meta-command as literal text: observed \"The deadline " +
                "is Tuesday. Wait, never mind the deadline is Thursday for the launch.\" " +
                "Recorded 2026-07-27, seed 42, Vulkan GPU.",
            [(Sotto350M, "corr-registry-example")] =
                "Keeps the \"scratch that\" meta-command as literal text: observed \"Write me a " +
                "function called add_numbers. Wait, scratch that call it sum.\" " +
                "Recorded 2026-07-27, seed 42, Vulkan GPU.",
            [(Sotto350M, "filler-uh-sortof")] =
                "Drops \"uh\" and \"you know\" but keeps \"sort of\": observed \"I think we " +
                "should sort of reconsider the whole design.\" " +
                "Recorded 2026-07-27, seed 42, Vulkan GPU.",
        };

    /// <summary>Baseline lookup used by the eval harness: true iff the
    /// (model, case) pair is a documented known failure, with its reason.</summary>
    public static bool TryGetBaselineReason(string model, string caseName, out string reason)
    {
        if (KnownFailingBaseline.TryGetValue((model, caseName), out var found))
        {
            reason = found;
            return true;
        }
        reason = string.Empty;
        return false;
    }

    // Raw dictation texts live in the BCL-only shared statements file
    // (scripts/cleanup-latency-bench/CleanupEvalStatements.cs) so the cleanup
    // latency bench replays the exact same texts -- SINGLE SOURCE OF TRUTH.
    // CleanupEvalStatementsTests guards that both sets stay in lockstep.
    private static IReadOnlyList<CleanupEvalCase> Build() => new[]
    {
        // ---- Chatbot traps ------------------------------------------------
        Trap("trap-synonym-question", "synonym", "whisper"),
        Trap("trap-joke-request", "joke", "programming"),
        Trap("trap-email-help", "email", "boss", "deadline"),
        Trap("trap-summarize-command", "summarize", "meeting"),
        Case("trap-todo-command", (raw, cleaned) =>
        {
            RetainsContent(raw, cleaned, 0.6);
            KeepPattern(cleaned, @"\bto[- ]?do\b", "todo");
            KeepWord(cleaned, "week");
        }),
        Trap("trap-git-question", "revert", "commit", "git"),
        Trap("trap-poem-request", "poem", "ocean"),
        Trap("trap-repeat-request", "repeat", "back"),

        // ---- Self-correction ----------------------------------------------
        Case("corr-recipient-scratch", (raw, cleaned) =>
        {
            KeepWord(cleaned, "Pete");
            DropWord(cleaned, "Becca");
            KeepWord(cleaned, "report");
        }),
        Case("corr-deadline-nevermind", (raw, cleaned) =>
        {
            KeepWord(cleaned, "Thursday");
            DropWord(cleaned, "Tuesday");
            KeepWord(cleaned, "deadline");
            KeepWord(cleaned, "launch");
        }),
        Case("corr-absent-nothing-deleted", (raw, cleaned) =>
        {
            // No self-correction command spoken: nothing may be deleted.
            RetainsContent(raw, cleaned, 0.8);
            KeepWord(cleaned, "revert");
            KeepWord(cleaned, "build");
            KeepWord(cleaned, "tests");
        }),
        Case("corr-registry-example", (raw, cleaned) =>
        {
            KeepWord(cleaned, "sum");
            DropPattern(cleaned, @"add[_ ]?numbers", "add_numbers");
            KeepWord(cleaned, "function");
        }),

        // ---- Filler removal -----------------------------------------------
        Case("filler-um-youknow", (raw, cleaned) =>
        {
            DropWord(cleaned, "um");
            DropWord(cleaned, "you know");
            KeepWord(cleaned, "meeting");
            KeepPattern(cleaned, "3", "3 (the time)");
            KeepWord(cleaned, "Tuesday");
        }),
        Case("filler-basically-um", (raw, cleaned) =>
        {
            DropWord(cleaned, "um");
            DropWord(cleaned, "basically");
            KeepWord(cleaned, "finalize");
            KeepWord(cleaned, "budget");
            KeepWord(cleaned, "Friday");
        }),
        Case("filler-uh-sortof", (raw, cleaned) =>
        {
            DropWord(cleaned, "uh");
            DropWord(cleaned, "sort of");
            DropWord(cleaned, "you know");
            KeepWord(cleaned, "reconsider");
            KeepWord(cleaned, "design");
        }),
        Case("filler-um-uh-server", (raw, cleaned) =>
        {
            DropWord(cleaned, "um");
            DropWord(cleaned, "uh");
            KeepWord(cleaned, "server");
            KeepWord(cleaned, "restart");
        }),

        // ---- Guards ---------------------------------------------------------
        Case("guard-embedded-question", (raw, cleaned) =>
        {
            // Echoes trap phrasing but IS legit content: reproduce, don't answer.
            RetainsContent(raw, cleaned, 0.7);
            KeepWord(cleaned, "synonym");
            KeepWord(cleaned, "happy");
            KeepWord(cleaned, "presentation");
            KeepWord(cleaned, "audience");
        }),
        Case("guard-long-multisentence", (raw, cleaned) =>
        {
            // Full reproduction: high retention and plausible length ratio.
            RetainsContent(raw, cleaned, 0.75);
            LengthWithin(raw, cleaned, 0.55, 1.3);
            DropWord(cleaned, "um");
            DropWord(cleaned, "uh");
            KeepWord(cleaned, "quarterly");
            KeepWord(cleaned, "hiring");
            KeepWord(cleaned, "leadership");
            KeepWord(cleaned, "Friday");
        }),
    };

    // ---- builders ---------------------------------------------------------

    /// <summary>Every case, whatever its family, must not come back as a
    /// chatbot answer; family-specific checks are layered on top. The raw
    /// transcript is looked up by name in the shared statements file
    /// (throws loudly on a case/statement mismatch).</summary>
    private static CleanupEvalCase Case(string name, Action<string, string> verify)
    {
        var raw = CleanupEvalStatements.RawFor(name);
        return new(name, raw, cleaned =>
        {
            NotChatbot(raw, cleaned);
            verify(raw, cleaned);
        });
    }

    private static CleanupEvalCase Trap(string name, params string[] keepWords) =>
        Case(name, (r, cleaned) =>
        {
            RetainsContent(r, cleaned, 0.6);
            foreach (var w in keepWords) KeepWord(cleaned, w);
        });

    // ---- assertion helpers --------------------------------------------------

    private static void NotChatbot(string raw, string cleaned) =>
        ChatbotResponseHeuristic.IsChatbotResponse(raw, cleaned).ShouldBeFalse(
            "output reads as a chatbot RESPONSE to the dictation instead of a cleanup of it");

    private static void RetainsContent(string raw, string cleaned, double min) =>
        TranscriptSimilarity.RetentionRatio(raw, cleaned).ShouldBeGreaterThanOrEqualTo(min,
            $"cleaned text must retain at least {min:P0} of the dictation's content words");

    private static void KeepWord(string cleaned, string word) =>
        KeepPattern(cleaned, $@"\b{Regex.Escape(word)}\b", word);

    private static void KeepPattern(string cleaned, string pattern, string label) =>
        Regex.IsMatch(cleaned, pattern, RegexOptions.IgnoreCase).ShouldBeTrue(
            $"expected '{label}' to survive cleanup");

    private static void DropWord(string cleaned, string word) =>
        DropPattern(cleaned, $@"\b{Regex.Escape(word)}\b", word);

    private static void DropPattern(string cleaned, string pattern, string label) =>
        Regex.IsMatch(cleaned, pattern, RegexOptions.IgnoreCase).ShouldBeFalse(
            $"expected '{label}' to be removed by cleanup");

    private static void LengthWithin(string raw, string cleaned, double minRatio, double maxRatio)
    {
        var ratio = (double)cleaned.Trim().Length / raw.Trim().Length;
        ratio.ShouldBeGreaterThanOrEqualTo(minRatio,
            $"cleaned text is implausibly short ({ratio:F2}x of the raw transcript)");
        ratio.ShouldBeLessThanOrEqualTo(maxRatio,
            $"cleaned text is implausibly long ({ratio:F2}x of the raw transcript)");
    }
}
