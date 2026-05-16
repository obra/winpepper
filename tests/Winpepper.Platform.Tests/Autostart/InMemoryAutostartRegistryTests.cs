using Shouldly;
using Winpepper.Platform.Autostart;
using Xunit;

namespace Winpepper.Platform.Tests.Autostart;

public class InMemoryAutostartRegistryTests
{
    [Fact]
    public void Initial_State_Is_Disabled()
    {
        var r = new InMemoryAutostartRegistry();
        r.IsEnabled().ShouldBeFalse();
    }

    [Fact]
    public void Enable_Then_IsEnabled_True()
    {
        var r = new InMemoryAutostartRegistry();
        r.Enable(@"C:\Program Files\Winpepper\winpepper.exe", "--tray");
        r.IsEnabled().ShouldBeTrue();
        r.CurrentCommand().ShouldBe("\"C:\\Program Files\\Winpepper\\winpepper.exe\" --tray");
    }

    [Fact]
    public void Disable_Removes_Value()
    {
        var r = new InMemoryAutostartRegistry();
        r.Enable("a.exe", "");
        r.Disable();
        r.IsEnabled().ShouldBeFalse();
        r.CurrentCommand().ShouldBeNull();
    }
}
