using Shouldly;
using Winpepper.Core.ViewModels;
using Winpepper.Corrections;
using Xunit;

namespace Winpepper.Corrections.Tests;

public class CorrectionsWiringTests : IDisposable
{
    private readonly string _path;

    public CorrectionsWiringTests()
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
    public void Vm_Add_RoundTrips_To_Disk()
    {
        var vm = CorrectionsWiring.CreateViewModel(new CorrectionStore(_path));

        vm.AddPreferred("ChatGPT").ShouldBeNull();
        vm.AddReplacement("chat gbt", "ChatGPT").ShouldBeNull();

        // Persistence is proven by a FRESH store over the same path, exactly
        // like the dictation pipeline reads it — not by in-memory state.
        var loaded = new CorrectionStore(_path).Load();
        loaded.Preferred.ShouldBe(new[] { "ChatGPT" });
        loaded.Replacements["chat gbt"].ShouldBe("ChatGPT");
    }

    [Fact]
    public void Vm_Seeds_From_Existing_Store()
    {
        new CorrectionStore(_path).Save(new CorrectionsData
        {
            Preferred = new[] { "ChatGPT", "Anthropic" },
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat gbt"] = "ChatGPT",
            },
        });

        var vm = CorrectionsWiring.CreateViewModel(new CorrectionStore(_path));

        vm.Preferred.Select(p => p.Text).ShouldBe(new[] { "ChatGPT", "Anthropic" });
        vm.Replacements.Count.ShouldBe(1);
        vm.Replacements[0].Wrong.ShouldBe("chat gbt");
        vm.Replacements[0].Right.ShouldBe("ChatGPT");
    }

    [Fact]
    public void Vm_Remove_Persists_The_Removal()
    {
        new CorrectionStore(_path).Save(new CorrectionsData
        {
            Preferred = new[] { "ChatGPT", "Anthropic" },
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat gbt"] = "ChatGPT",
            },
        });
        var vm = CorrectionsWiring.CreateViewModel(new CorrectionStore(_path));

        vm.RemovePreferred(vm.Preferred.Single(p => p.Text == "ChatGPT"));
        vm.RemoveReplacement(vm.Replacements[0]);

        var loaded = new CorrectionStore(_path).Load();
        loaded.Preferred.ShouldBe(new[] { "Anthropic" });
        loaded.Replacements.ShouldBeEmpty();
    }
}
