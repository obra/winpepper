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

    private static CorrectionsViewModel NewVm()
        => new(new List<string>(), new Dictionary<string, string>(), (_, _) => { });
}
