using Shouldly;
using Winpepper.Core.Diagnostics;
using Winpepper.Core.Sessions;
using Winpepper.Core.Threading;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

[Trait("Layer", "ViewModel")]
public class SessionViewModelCpuPeggedTests
{
    private static (SessionViewModel vm, SessionEngine engine) NewVm()
    {
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        return (vm, engine);
    }

    // GetSystemTimes semantics: kernel INCLUDES idle. busy = (kernel-idle)+user.
    // Baseline (0,0,0) -> sample (idle,kernel,user) gives pct = busy*100/total.
    private static (long, long, long) Sample(long idle, long kernel, long user)
        => (idle, kernel, user);

    [Fact]
    public void Pegged_When_Busy_At_Or_Above_Threshold_On_The_Sample_Tick()
    {
        var (vm, engine) = NewVm();
        var samples = new Queue<(long, long, long)?>(new (long, long, long)?[]
        {
            Sample(0, 0, 0),          // baseline at recording start
            Sample(25, 100, 0),       // busy = 75, total = 100 -> 75% (at threshold)
        });
        vm.SystemTimesSampler = () => samples.Dequeue();

        engine.Apply(SessionEvent.StartRequested);   // -> Recording, takes baseline
        vm.CpuPegged.ShouldBeNull();                 // no decision yet

        for (var i = 0; i < CpuPeggedPolicy.SampleAfterTicks - 1; i++) vm.Tick();
        vm.CpuPegged.ShouldBeNull();                 // still inside the window

        vm.Tick();                                   // tick #4: sample + decide
        vm.CpuPegged.ShouldBe(true);
    }

    [Fact]
    public void Not_Pegged_Below_Threshold_And_Decision_Sticks_For_The_Dictation()
    {
        var (vm, engine) = NewVm();
        var calls = 0;
        vm.SystemTimesSampler = () =>
        {
            calls++;
            return calls == 1 ? Sample(0, 0, 0) : Sample(90, 100, 0); // busy=10 -> 10%
        };

        engine.Apply(SessionEvent.StartRequested);
        for (var i = 0; i < CpuPeggedPolicy.SampleAfterTicks; i++) vm.Tick();
        vm.CpuPegged.ShouldBe(false);

        var callsAtDecision = calls;
        vm.Tick();
        vm.Tick();
        calls.ShouldBe(callsAtDecision);             // decided once, never resampled
        vm.CpuPegged.ShouldBe(false);
    }

    [Fact]
    public void No_Sampler_Or_No_Reading_Leaves_CpuPegged_Null()
    {
        var (vmNoSampler, engine1) = NewVm();
        engine1.Apply(SessionEvent.StartRequested);
        for (var i = 0; i < CpuPeggedPolicy.SampleAfterTicks + 2; i++) vmNoSampler.Tick();
        vmNoSampler.CpuPegged.ShouldBeNull();

        var (vmNullReading, engine2) = NewVm();
        vmNullReading.SystemTimesSampler = () => null; // off-Windows / API failure
        engine2.Apply(SessionEvent.StartRequested);
        for (var i = 0; i < CpuPeggedPolicy.SampleAfterTicks + 2; i++) vmNullReading.Tick();
        vmNullReading.CpuPegged.ShouldBeNull();
    }

    [Fact]
    public void Next_Recording_Resets_The_Decision()
    {
        var (vm, engine) = NewVm();
        var q = new Queue<(long, long, long)?>(new (long, long, long)?[]
        {
            Sample(0, 0, 0), Sample(0, 100, 100),    // dictation 1: busy=200/total=200 -> 100%
            Sample(0, 200, 100), Sample(190, 400, 100), // dictation 2: idleΔ=190 kernelΔ=200 userΔ=0 -> busy=10 -> 5%
        });
        vm.SystemTimesSampler = () => q.Dequeue();

        engine.Apply(SessionEvent.StartRequested);
        for (var i = 0; i < CpuPeggedPolicy.SampleAfterTicks; i++) vm.Tick();
        vm.CpuPegged.ShouldBe(true);

        // Walk the engine back to Idle the way the pipeline does, then start again.
        engine.Apply(SessionEvent.StopRequested);
        engine.Apply(SessionEvent.TranscriptReady);     // -> Injecting
        engine.Apply(SessionEvent.InjectionCompleted);  // -> Idle (verified against SessionEvent.cs and SessionViewModelTests.cs:48-54)
        engine.State.ShouldBe(SessionState.Idle);

        engine.Apply(SessionEvent.StartRequested);   // dictation 2
        vm.CpuPegged.ShouldBeNull();                 // reset at recording start
        for (var i = 0; i < CpuPeggedPolicy.SampleAfterTicks; i++) vm.Tick();
        vm.CpuPegged.ShouldBe(false);
    }
}
