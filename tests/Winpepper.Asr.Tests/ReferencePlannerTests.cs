using AsrEvalCorpus;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class ReferencePlannerTests
{
    private static CorpusEntry Entry(bool expectedSilent = false, bool exclude = false) =>
        new("abc123", new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc), 1000, "clips/abc123.wav",
            "r", "c", "m", "n", new ClipTimings(1, 1, 1, 1, 1))
        { ExpectedSilent = expectedSilent, Exclude = exclude };

    [Fact]
    public void Decide_ExcludedClip_IsAlwaysSkipped()
    {
        ReferencePlanner.Decide(Entry(exclude: true), referenceExists: false, force: false)
            .ShouldBe(ReferenceAction.Skip);
        ReferencePlanner.Decide(Entry(exclude: true), referenceExists: false, force: true)
            .ShouldBe(ReferenceAction.Skip);
    }

    [Fact]
    public void Decide_ExpectedSilent_WritesEmptyUnlessAlreadyPresent()
    {
        ReferencePlanner.Decide(Entry(expectedSilent: true), referenceExists: false, force: false)
            .ShouldBe(ReferenceAction.WriteEmpty);
        ReferencePlanner.Decide(Entry(expectedSilent: true), referenceExists: true, force: false)
            .ShouldBe(ReferenceAction.Skip);
        ReferencePlanner.Decide(Entry(expectedSilent: true), referenceExists: true, force: true)
            .ShouldBe(ReferenceAction.WriteEmpty);
    }

    [Fact]
    public void Decide_NormalClip_TranscribesWhenMissingOrForced()
    {
        ReferencePlanner.Decide(Entry(), referenceExists: false, force: false)
            .ShouldBe(ReferenceAction.Transcribe);
        ReferencePlanner.Decide(Entry(), referenceExists: true, force: false)
            .ShouldBe(ReferenceAction.Skip);
        ReferencePlanner.Decide(Entry(), referenceExists: true, force: true)
            .ShouldBe(ReferenceAction.Transcribe);
    }

    [Fact]
    public void ReferencePath_SitsNextToTheClip()
    {
        var path = ReferencePlanner.ReferencePath("/corpus", Entry());

        path.Replace('\\', '/').ShouldBe("/corpus/clips/abc123.reference.txt");
    }
}
