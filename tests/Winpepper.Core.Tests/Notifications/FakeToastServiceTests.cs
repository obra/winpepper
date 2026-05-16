using Shouldly;
using Winpepper.Core.Notifications;
using Xunit;

namespace Winpepper.Core.Tests.Notifications;

public class FakeToastServiceTests
{
    [Fact]
    public async Task ShowAsync_Returns_Default_Button_Tag_After_Timeout()
    {
        var fake = new FakeToastService();
        fake.AutoSelect("");
        var result = await fake.ShowAsync(
            "title", "body",
            new[] { new ToastButton("yes", "Yes"), new ToastButton("no", "No") },
            timeout: TimeSpan.FromMilliseconds(10));
        result.ShouldBe("");
    }

    [Fact]
    public async Task ShowAsync_Returns_Selected_Button_Tag()
    {
        var fake = new FakeToastService();
        fake.AutoSelect("yes");
        var result = await fake.ShowAsync("t", "b",
            new[] { new ToastButton("yes", "Yes"), new ToastButton("no", "No") },
            timeout: TimeSpan.FromSeconds(30));
        result.ShouldBe("yes");
    }

    [Fact]
    public async Task ShowAsync_Records_Last_Call_For_Assertion()
    {
        var fake = new FakeToastService();
        fake.AutoSelect("no");
        await fake.ShowAsync("title", "body",
            new[] { new ToastButton("yes", "Yes"), new ToastButton("no", "No") },
            timeout: TimeSpan.FromSeconds(1));

        fake.Calls.Count.ShouldBe(1);
        fake.Calls[0].Title.ShouldBe("title");
        fake.Calls[0].Body.ShouldBe("body");
        fake.Calls[0].Buttons.Length.ShouldBe(2);
    }
}
