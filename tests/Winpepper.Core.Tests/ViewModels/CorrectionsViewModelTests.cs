using Shouldly;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

[Trait("Layer", "ViewModel")]
public class CorrectionsViewModelTests
{
    [Fact]
    public void AddPreferred_Adds_Valid_Entry()
    {
        var vm = NewVm();
        vm.AddPreferred("ChatGPT").ShouldBeNull();
        vm.Preferred.Count.ShouldBe(1);
        vm.Preferred[0].Text.ShouldBe("ChatGPT");
        vm.Preferred[0].Error.ShouldBeNull();
    }

    [Fact]
    public void AddPreferred_Rejects_Short_String()
    {
        var vm = NewVm();
        vm.AddPreferred("a")!.ShouldContain("at least 2");
        vm.Preferred.Count.ShouldBe(0);
    }

    [Fact]
    public void AddPreferred_Rejects_Empty()
    {
        var vm = NewVm();
        vm.AddPreferred("  ")!.ShouldContain("empty");
        vm.Preferred.Count.ShouldBe(0);
    }

    [Fact]
    public void AddPreferred_Rejects_Duplicate()
    {
        var vm = NewVm();
        vm.AddPreferred("ChatGPT");
        vm.AddPreferred("ChatGPT")!.ShouldContain("duplicate");
        vm.Preferred.Count.ShouldBe(1);
    }

    [Fact]
    public void AddReplacement_Adds_Valid_Pair()
    {
        var vm = NewVm();
        vm.AddReplacement("chat gbt", "ChatGPT").ShouldBeNull();
        vm.Replacements.Count.ShouldBe(1);
        vm.Replacements[0].Wrong.ShouldBe("chat gbt");
        vm.Replacements[0].Right.ShouldBe("ChatGPT");
    }

    [Fact]
    public void AddReplacement_Rejects_Self_Mapping()
    {
        var vm = NewVm();
        vm.AddReplacement("chatgpt", "ChatGPT").ShouldBeNull(); // case differs → allowed
        vm.AddReplacement("chatgpt", "chatgpt")!.ShouldContain("same");
    }

    [Fact]
    public void AddReplacement_Rejects_Short_Sides()
    {
        var vm = NewVm();
        vm.AddReplacement("a", "ChatGPT")!.ShouldContain("at least 2");
        vm.AddReplacement("ChatGPT", "b")!.ShouldContain("at least 2");
    }

    [Fact]
    public void Remove_Removes_Entry()
    {
        var vm = NewVm();
        vm.AddPreferred("ChatGPT");
        vm.AddPreferred("Anthropic");
        vm.RemovePreferred(vm.Preferred[0]);
        vm.Preferred.Count.ShouldBe(1);
        vm.Preferred[0].Text.ShouldBe("Anthropic");
    }

    [Fact]
    public void Adds_Trigger_Persist_Callback()
    {
        var saves = 0;
        var vm = new CorrectionsViewModel(
            new List<string>(), new Dictionary<string, string>(),
            (_, _) => saves++);
        vm.AddPreferred("ChatGPT");
        vm.AddReplacement("chat gbt", "ChatGPT");
        saves.ShouldBe(2);
    }

    [Fact]
    public void AddReplacement_Inserts_NewestFirst()
    {
        var vm = NewVm();
        vm.AddReplacement("chat gbt", "ChatGPT").ShouldBeNull();
        vm.AddReplacement("ann thropic", "Anthropic").ShouldBeNull();

        vm.Replacements.Select(r => r.Wrong)
            .ShouldBe(new[] { "ann thropic", "chat gbt" });
    }

    [Fact]
    public void Ctor_Seeds_Replacements_NewestFirst()
    {
        // Disk order is oldest-first (new entries are appended at the END of
        // corrections.json by both Persist() and the post-paste learning
        // writer), so the LAST seeded pair is the newest and must render first.
        var vm = new CorrectionsViewModel(
            new List<string>(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat gbt"] = "ChatGPT",
                ["ann thropic"] = "Anthropic",
            },
            (_, _) => { });

        vm.Replacements.Select(r => r.Wrong)
            .ShouldBe(new[] { "ann thropic", "chat gbt" });
    }

    [Fact]
    public void RemoveReplacement_Preserves_NewestFirst_Order_Of_Survivors()
    {
        var vm = NewVm();
        vm.AddReplacement("chat gbt", "ChatGPT").ShouldBeNull();
        vm.AddReplacement("ann thropic", "Anthropic").ShouldBeNull();
        vm.AddReplacement("open ai", "OpenAI").ShouldBeNull();

        vm.RemoveReplacement(vm.Replacements[1]); // the middle entry ("ann thropic")

        vm.Replacements.Select(r => r.Wrong)
            .ShouldBe(new[] { "open ai", "chat gbt" });
    }

    [Fact]
    public void Persist_Writes_Replacements_OldestFirst()
    {
        // The DISPLAY order is newest-first, but corrections.json stays
        // canonical oldest-first/append-at-end so the file byte-order, the
        // cleanup prompt hint order, the AssemblyAI custom_spelling order,
        // and the post-paste learning writer's append semantics are all
        // unchanged. This test pins that: it passes today and must STILL
        // pass after the newest-first change (it fails a naive
        // Insert(0)-only implementation that forgets to reverse in Persist).
        IReadOnlyDictionary<string, string>? captured = null;
        var vm = new CorrectionsViewModel(
            new List<string>(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat gbt"] = "ChatGPT",
            },
            (_, r) => captured = r);

        vm.AddReplacement("ann thropic", "Anthropic").ShouldBeNull();

        captured.ShouldNotBeNull();
        captured!.Keys.ShouldBe(new[] { "chat gbt", "ann thropic" });
    }

    private static CorrectionsViewModel NewVm()
        => new(new List<string>(), new Dictionary<string, string>(), (_, _) => { });
}
