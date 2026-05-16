using Shouldly;
using Winpepper.Platform.Learning;
using Xunit;

namespace Winpepper.Platform.Tests.Learning;

public class FocusedElementSnapshotTests
{
    [Fact]
    public void Empty_Snapshot_Has_IsValid_False()
    {
        FocusedElementSnapshot.Empty.IsValid.ShouldBeFalse();
        FocusedElementSnapshot.Empty.ElementId.ShouldBe("");
    }

    [Fact]
    public void Snapshot_With_Element_Id_Is_Valid()
    {
        var snap = new FocusedElementSnapshot
        {
            ForegroundHwnd = new IntPtr(0x1234),
            ElementId = "42.7",
            WindowTitle = "Notepad",
        };
        snap.IsValid.ShouldBeTrue();
    }
}
