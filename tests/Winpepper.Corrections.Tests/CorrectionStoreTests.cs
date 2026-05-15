using Shouldly;
using Winpepper.Corrections;
using Xunit;

namespace Winpepper.Corrections.Tests;

public class CorrectionStoreTests : IDisposable
{
    private readonly string _path;

    public CorrectionStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"corrections-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), $"{Path.GetFileName(_path)}.tmp-*"))
            File.Delete(f);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var store = new CorrectionStore(_path);
        var data = store.Load();
        data.Schema.ShouldBe(CorrectionsData.CurrentSchema);
        data.Preferred.ShouldBeEmpty();
        data.Replacements.ShouldBeEmpty();
    }

    [Fact]
    public void Save_Then_Load_RoundTrips()
    {
        var store = new CorrectionStore(_path);
        var data = new CorrectionsData
        {
            Preferred = new[] { "ChatGPT", "Anthropic" },
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat gbt"] = "ChatGPT",
                ["ann thropic"] = "Anthropic",
            },
        };
        store.Save(data);

        var loaded = new CorrectionStore(_path).Load();
        loaded.Preferred.ShouldBe(new[] { "ChatGPT", "Anthropic" });
        loaded.Replacements["chat gbt"].ShouldBe("ChatGPT");
        loaded.Replacements["ann thropic"].ShouldBe("Anthropic");
    }

    [Fact]
    public void Load_BadJson_FallsBackToEmpty()
    {
        File.WriteAllText(_path, "{ not json");
        var data = new CorrectionStore(_path).Load();
        data.ShouldBe(CorrectionsData.Empty);
    }

    [Fact]
    public void Load_FutureSchema_FallsBackToEmpty()
    {
        File.WriteAllText(_path, """{ "schema": 999, "preferred": [], "replacements": {} }""");
        var data = new CorrectionStore(_path).Load();
        data.Schema.ShouldBe(CorrectionsData.CurrentSchema);
        data.Preferred.ShouldBeEmpty();
    }

    [Fact]
    public void Save_DoesNotLeave_TempFile()
    {
        var store = new CorrectionStore(_path);
        store.Save(CorrectionsData.Empty);
        Directory.GetFiles(Path.GetDirectoryName(_path)!, $"{Path.GetFileName(_path)}.tmp-*")
            .Length.ShouldBe(0);
    }

    [Fact]
    public void AddPreferred_AppendsUnique_AndPersists()
    {
        var store = new CorrectionStore(_path);
        store.AddPreferred("ChatGPT").ShouldBeTrue();
        store.AddPreferred("ChatGPT").ShouldBeFalse(); // duplicate (Ordinal compare)
        store.AddPreferred("Anthropic").ShouldBeTrue();

        var loaded = new CorrectionStore(_path).Load();
        loaded.Preferred.ShouldBe(new[] { "ChatGPT", "Anthropic" });
    }

    [Fact]
    public void AddPreferred_RejectsInvalid()
    {
        var store = new CorrectionStore(_path);
        store.AddPreferred("a").ShouldBeFalse(); // too short
        store.AddPreferred(" ").ShouldBeFalse();
        new CorrectionStore(_path).Load().Preferred.ShouldBeEmpty();
    }

    [Fact]
    public void AddReplacement_StoresAndPersists()
    {
        var store = new CorrectionStore(_path);
        store.AddReplacement("chat gbt", "ChatGPT").ShouldBeTrue();
        store.AddReplacement("chat gbt", "chat gbt").ShouldBeFalse(); // self-mapping rejected
        store.AddReplacement("chat gbt", "ChatGPT-NewMapping").ShouldBeTrue(); // overwrite is allowed

        var loaded = new CorrectionStore(_path).Load();
        loaded.Replacements["chat gbt"].ShouldBe("ChatGPT-NewMapping");
    }
}
