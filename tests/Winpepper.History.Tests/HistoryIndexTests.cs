using Shouldly;
using System.Text.Json;
using Xunit;

namespace Winpepper.History.Tests;

public class HistoryIndexTests
{
    [Fact]
    public void Empty_HasSchemaVersion_AndNoEntries()
    {
        var idx = new HistoryIndex();
        idx.Schema.ShouldBe(1);
        idx.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void RoundTrips_With_TwoEntries()
    {
        var original = new HistoryIndex
        {
            Entries = new List<HistoryEntry>
            {
                new() { Id = "a", RawTranscript = "alpha" },
                new() { Id = "b", RawTranscript = "beta" },
            }
        };
        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<HistoryIndex>(json)!;
        loaded.Schema.ShouldBe(1);
        loaded.Entries.Count.ShouldBe(2);
        loaded.Entries[0].RawTranscript.ShouldBe("alpha");
        loaded.Entries[1].RawTranscript.ShouldBe("beta");
    }

    [Fact]
    public void OlderSchema_LoadStillReturnsEntries()
    {
        // A future migration would convert. For now we accept and pass through.
        var json = """{"schema":1,"entries":[{"id":"x","rawTranscript":"hi"}]}""";
        var loaded = JsonSerializer.Deserialize<HistoryIndex>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        loaded.Entries.Count.ShouldBe(1);
        loaded.Entries[0].Id.ShouldBe("x");
    }
}
