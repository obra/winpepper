using Shouldly;
using Winpepper.Core.Errors;
using Winpepper.Core.Sessions;
using Winpepper.Core.Threading;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public class SessionViewModelErrorBusTests
{
    [Fact]
    public void Vm_Updates_LastError_When_ErrorBus_Reports()
    {
        var bus = new ErrorBus();
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        vm.AttachErrorBus(bus);

        bus.Report(ErrorStage.Audio, new InvalidOperationException("mic gone"), Guid.NewGuid());

        vm.LastErrorStage.ShouldBe(ErrorStage.Audio);
        vm.LastErrorMessage.ShouldBe("mic gone");
    }

    [Fact]
    public void Vm_Sets_Stage_To_Error_On_Bus_Report()
    {
        var bus = new ErrorBus();
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        vm.AttachErrorBus(bus);

        bus.Report(ErrorStage.Asr, new InvalidOperationException("load fail"), Guid.NewGuid());

        vm.Stage.ShouldBe(SessionStage.Error);
        vm.StatusText.ShouldContain("load fail");
    }
}
