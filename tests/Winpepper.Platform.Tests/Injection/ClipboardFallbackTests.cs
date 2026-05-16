using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class ClipboardFallbackTests
{
    [Fact]
    public void Copy_Calls_Clipboard_With_Exact_String()
    {
        var clip = new FakeClipboard();
        var fb = new ClipboardFallback(clip);
        fb.Copy("hello world");
        clip.LastSetText.ShouldBe("hello world");
    }

    [Fact]
    public void Copy_Empty_String_Is_NoOp()
    {
        var clip = new FakeClipboard();
        var fb = new ClipboardFallback(clip);
        fb.Copy("");
        clip.LastSetText.ShouldBeNull();
    }

    [Fact]
    public void Copy_Wraps_Exceptions_And_Returns_False()
    {
        var clip = new ThrowingClipboard();
        var fb = new ClipboardFallback(clip);
        fb.Copy("x").ShouldBeFalse();
    }

    private sealed class FakeClipboard : IClipboard
    {
        public string? LastSetText { get; private set; }
        public bool SetText(string text) { LastSetText = text; return true; }
    }

    private sealed class ThrowingClipboard : IClipboard
    {
        public bool SetText(string text) => throw new InvalidOperationException("denied");
    }
}
