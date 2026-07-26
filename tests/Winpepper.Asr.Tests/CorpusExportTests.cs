using AsrEvalCorpus;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class CorpusExportTests
{
    private static HistoryIndexEntry History(string id, int minuteOffset = 0) =>
        new(id, new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc).AddMinutes(minuteOffset),
            "raw", "clean", $"2026-07-26/{id}.wav", 2000, "nemotron-streaming-en", "none",
            new ClipTimings(2000, 400, 0, 10, 2500));

    private static CorpusManifest ManifestWith(params string[] ids)
    {
        var m = new CorpusManifest();
        foreach (var id in ids)
            m.Entries.Add(new CorpusEntry(id, DateTime.UtcNow, 1, $"clips/{id}.wav",
                "r", "c", "m", "n", new ClipTimings(1, 1, 1, 1, 1)));
        return m;
    }

    [Fact]
    public void BuildPlan_EmptyManifest_MapsEveryHistoryEntryToCorpusEntry()
    {
        var plan = CorpusExport.BuildPlan(new[] { History("aaa") }, new CorpusManifest());

        plan.SkippedExisting.ShouldBe(0);
        var item = plan.ToAdd.Single();
        item.Source.Id.ShouldBe("aaa");
        item.Entry.Id.ShouldBe("aaa");
        item.Entry.WavPath.ShouldBe("clips/aaa.wav");
        item.Entry.RawTranscript.ShouldBe("raw");
        item.Entry.CleanedText.ShouldBe("clean");
        item.Entry.AsrModelName.ShouldBe("nemotron-streaming-en");
        item.Entry.Timings.TranscribeMs.ShouldBe(400);
        item.Entry.ExpectedSilent.ShouldBeFalse();
        item.Entry.Exclude.ShouldBeFalse();
    }

    [Fact]
    public void BuildPlan_ExistingIds_AreSkippedNotDuplicated()
    {
        var plan = CorpusExport.BuildPlan(
            new[] { History("aaa"), History("bbb") }, ManifestWith("aaa"));

        plan.SkippedExisting.ShouldBe(1);
        plan.ToAdd.Single().Entry.Id.ShouldBe("bbb");
    }

    [Fact]
    public void BuildPlan_Take_LimitsToTheMostRecentNewClips()
    {
        var plan = CorpusExport.BuildPlan(
            new[] { History("old", 0), History("mid", 1), History("new", 2) },
            new CorpusManifest(), take: 2);

        plan.ToAdd.Count.ShouldBe(2);
        plan.ToAdd[0].Entry.Id.ShouldBe("new");
        plan.ToAdd[1].Entry.Id.ShouldBe("mid");
    }
}
