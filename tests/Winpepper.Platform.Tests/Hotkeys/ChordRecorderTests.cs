using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

public class ChordRecorderTests
{
    [Fact]
    public void NewRecorder_IsIdle_AndIgnoresKeys()
    {
        var r = new ChordRecorder();
        r.IsRecording.ShouldBeFalse();
        r.OnKey("A", "LeftCtrl+", isEscape: false).ShouldBe(ChordKeyResult.Ignored);
        r.CommittedChord.ShouldBeNull();
    }

    [Fact]
    public void Begin_EntersRecording_AndClearsPreviousCommit()
    {
        var r = new ChordRecorder();
        r.Begin();
        r.OnKey("A", "LeftCtrl+", isEscape: false).ShouldBe(ChordKeyResult.Committed);
        r.CommittedChord.ShouldBe("LeftCtrl+A");

        r.Begin();
        r.IsRecording.ShouldBeTrue();
        r.CommittedChord.ShouldBeNull();
    }

    [Fact]
    public void Escape_CancelsRecording_WithoutCommitting()
    {
        var r = new ChordRecorder();
        r.Begin();
        r.OnKey("Esc", "", isEscape: true).ShouldBe(ChordKeyResult.Cancelled);
        r.IsRecording.ShouldBeFalse();
        r.CommittedChord.ShouldBeNull();
    }

    [Fact]
    public void Escape_WithModifiersHeld_StillCancels()
    {
        var r = new ChordRecorder();
        r.Begin();
        r.OnKey(null, "LeftCtrl+LeftShift+", isEscape: true).ShouldBe(ChordKeyResult.Cancelled);
        r.IsRecording.ShouldBeFalse();
    }

    [Fact]
    public void Cancel_WhileRecording_ReturnsTrue_AndStops()
    {
        var r = new ChordRecorder();
        r.Begin();
        r.Cancel().ShouldBeTrue();
        r.IsRecording.ShouldBeFalse();
        // Subsequent keys are ignored.
        r.OnKey("A", "", isEscape: false).ShouldBe(ChordKeyResult.Ignored);
    }

    [Fact]
    public void Cancel_WhenIdle_IsNoOp()
    {
        var r = new ChordRecorder();
        r.Cancel().ShouldBeFalse();
    }

    [Fact]
    public void UnmappedKey_IsIgnored_AndRecordingStaysArmed()
    {
        var r = new ChordRecorder();
        r.Begin();
        r.OnKey(null, "LeftCtrl+", isEscape: false).ShouldBe(ChordKeyResult.Ignored);
        r.IsRecording.ShouldBeTrue();
    }

    [Fact]
    public void UnparseableCombination_ReturnsInvalid_AndRecordingStaysArmed()
    {
        var r = new ChordRecorder();
        r.Begin();
        r.OnKey("NotARealKey", "", isEscape: false).ShouldBe(ChordKeyResult.Invalid);
        r.IsRecording.ShouldBeTrue();
        r.CommittedChord.ShouldBeNull();

        // A valid chord can still be committed afterwards.
        r.OnKey("F5", "LeftAlt+", isEscape: false).ShouldBe(ChordKeyResult.Committed);
        r.CommittedChord.ShouldBe("LeftAlt+F5");
    }

    [Fact]
    public void Committed_StopsRecording()
    {
        var r = new ChordRecorder();
        r.Begin();
        r.OnKey("Space", "Ctrl+Shift+", isEscape: false).ShouldBe(ChordKeyResult.Committed);
        r.IsRecording.ShouldBeFalse();
        r.OnKey("A", "", isEscape: false).ShouldBe(ChordKeyResult.Ignored);
    }
}
