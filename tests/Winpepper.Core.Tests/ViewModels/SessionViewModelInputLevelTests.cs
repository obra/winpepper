using System.ComponentModel;
using Shouldly;
using Winpepper.Core.Sessions;
using Winpepper.Core.Threading;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public class SessionViewModelInputLevelTests
{
    private static (SessionViewModel vm, SessionEngine engine) NewVm()
    {
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        return (vm, engine);
    }

    [Fact]
    public void InputLevel_StartsAtZero()
    {
        var (vm, _) = NewVm();
        vm.InputLevel.ShouldBe(0.0, 0.0001);
    }

    [Fact]
    public void ReportAudioFrame_WhileRecording_RaisesInputLevel()
    {
        var (vm, engine) = NewVm();
        engine.Apply(SessionEvent.StartRequested); // -> Recording
        vm.Stage.ShouldBe(SessionStage.Recording);

        var raised = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SessionViewModel.InputLevel)) raised = true; };

        vm.ReportAudioFrame(new float[] { 0.8f });

        raised.ShouldBeTrue();
        vm.InputLevel.ShouldBeGreaterThan(0.0);
    }

    [Fact]
    public void ReportAudioFrame_WhenNotRecording_IsIgnored()
    {
        var (vm, _) = NewVm(); // stays Idle
        vm.ReportAudioFrame(new float[] { 0.9f });
        vm.InputLevel.ShouldBe(0.0, 0.0001);
    }

    [Fact]
    public void LeavingRecordingStage_ResetsInputLevelToZero()
    {
        var (vm, engine) = NewVm();
        engine.Apply(SessionEvent.StartRequested); // Recording
        vm.ReportAudioFrame(new float[] { 0.9f });
        vm.InputLevel.ShouldBeGreaterThan(0.0);

        engine.Apply(SessionEvent.StopRequested); // -> Transcribing
        vm.Stage.ShouldBe(SessionStage.Transcribing);
        vm.InputLevel.ShouldBe(0.0, 0.0001);
    }
}
