using Shouldly;
using Winpepper.Core.Learning;
using Winpepper.Core.Notifications;
using Xunit;

namespace Winpepper.Core.Tests.Learning;

public class ToastPostPasteToastPromptTests
{
    [Fact]
    public async Task Yes_Tag_Maps_To_PostPasteDecisionYes()
    {
        var fake = new FakeToastService();
        fake.AutoSelect("yes");
        var p = new ToastPostPasteToastPrompt(fake);
        var r = await p.AskAsync(new LearningCandidate("chat gbt", "ChatGPT"), CancellationToken.None);
        r.ShouldBe(PostPasteDecision.Yes);
    }

    [Fact]
    public async Task Preferred_Tag_Maps_To_PostPasteDecisionPreferred()
    {
        var fake = new FakeToastService();
        fake.AutoSelect("preferred");
        var p = new ToastPostPasteToastPrompt(fake);
        var r = await p.AskAsync(new LearningCandidate("chat gbt", "ChatGPT"), CancellationToken.None);
        r.ShouldBe(PostPasteDecision.Preferred);
    }

    [Fact]
    public async Task No_Tag_Maps_To_PostPasteDecisionNo()
    {
        var fake = new FakeToastService();
        fake.AutoSelect("no");
        var p = new ToastPostPasteToastPrompt(fake);
        var r = await p.AskAsync(new LearningCandidate("chat gbt", "ChatGPT"), CancellationToken.None);
        r.ShouldBe(PostPasteDecision.No);
    }

    [Fact]
    public async Task Timeout_Returns_No()
    {
        var fake = new FakeToastService();
        fake.AutoSelect("");
        var p = new ToastPostPasteToastPrompt(fake);
        var r = await p.AskAsync(new LearningCandidate("chat gbt", "ChatGPT"), CancellationToken.None);
        r.ShouldBe(PostPasteDecision.No);
    }

    [Fact]
    public async Task Body_Includes_Wrong_And_Right_Strings()
    {
        var fake = new FakeToastService();
        fake.AutoSelect("no");
        var p = new ToastPostPasteToastPrompt(fake);
        await p.AskAsync(new LearningCandidate("chat gbt", "ChatGPT"), CancellationToken.None);
        fake.Calls[0].Body.ShouldContain("chat gbt");
        fake.Calls[0].Body.ShouldContain("ChatGPT");
    }
}
