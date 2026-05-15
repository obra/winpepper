using Shouldly;
using Winpepper.Core.Sessions;
using Winpepper.Core.Threading;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

[Trait("Layer", "ViewModel")]
public class SessionViewModelTests
{
    [Fact]
    public void Initial_Stage_Is_Idle()
    {
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        vm.Stage.ShouldBe(SessionStage.Idle);
        vm.StatusText.ShouldBe("Ready");
    }

    [Fact]
    public void Engine_StartRequested_Updates_Stage_To_Recording()
    {
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        engine.Apply(SessionEvent.StartRequested);
        vm.Stage.ShouldBe(SessionStage.Recording);
        vm.StatusText.ShouldBe("Recording...");
    }

    [Fact]
    public void Stage_Change_Raises_PropertyChanged()
    {
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        engine.Apply(SessionEvent.StartRequested);
        changed.ShouldContain(nameof(SessionViewModel.Stage));
        changed.ShouldContain(nameof(SessionViewModel.StatusText));
    }

    [Fact]
    public void Stages_Cycle_Through_Pipeline()
    {
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        engine.Apply(SessionEvent.StartRequested);
        vm.Stage.ShouldBe(SessionStage.Recording);
        engine.Apply(SessionEvent.StopRequested);
        vm.Stage.ShouldBe(SessionStage.Transcribing);
        engine.Apply(SessionEvent.TranscriptReady);
        vm.Stage.ShouldBe(SessionStage.Injecting);
        engine.Apply(SessionEvent.InjectionCompleted);
        vm.Stage.ShouldBe(SessionStage.Idle);
    }

    [Fact]
    public void NotifyError_Sets_ErrorStage_With_Message()
    {
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        vm.NotifyError("mic missing");
        vm.Stage.ShouldBe(SessionStage.Error);
        vm.StatusText.ShouldBe("Error: mic missing");
    }
}
