using Shouldly;
using Winpepper.Core.Settings;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

[Trait("Layer", "ViewModel")]
public class OnboardingViewModelTests
{
    private sealed class FakeProvisioner : IAsrProvisioningService
    {
        public AsrProvisioningState State { get; private set; } = new(AsrProvisioningStatus.Missing);
        public bool VerificationResult { get; set; } = true;
        public Exception? EnsureError { get; set; }
        public int EnsureCalls { get; private set; }
        public int VerifyCalls { get; private set; }

        public event EventHandler<AsrProvisioningState>? StateChanged;

        public Task EnsureReadyAsync(CancellationToken ct)
        {
            EnsureCalls++;
            if (EnsureError is not null) throw EnsureError;
            Publish(new AsrProvisioningState(AsrProvisioningStatus.Ready, 100));
            return Task.CompletedTask;
        }

        public Task<bool> VerifyReadyAsync(CancellationToken ct)
        {
            VerifyCalls++;
            Publish(new AsrProvisioningState(
                VerificationResult ? AsrProvisioningStatus.Ready : AsrProvisioningStatus.Missing,
                VerificationResult ? 100 : 0));
            return Task.FromResult(VerificationResult);
        }

        public void Publish(AsrProvisioningState state)
        {
            State = state;
            StateChanged?.Invoke(this, state);
        }
    }

    private sealed class FakeWriter : ISettingsWriter
    {
        public AppSettings Current = new();
        public int Flushes;
        public void Queue(Func<AppSettings, AppSettings> m) => Current = m(Current);
        public Task FlushAsync() { Flushes++; return Task.CompletedTask; }
    }

    private sealed class PermissiveValidator : IHotkeyValidator
    {
        public string? Validate(string chord, bool allowLongPressSpace = false) => null;
        public bool Clash(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);
    }

    private sealed class FakeValidator : IHotkeyValidator
    {
        private readonly HashSet<string> _conflicting;
        public FakeValidator(params string[] conflicting) =>
            _conflicting = new HashSet<string>(conflicting, StringComparer.Ordinal);
        public string? Validate(string chord, bool allowLongPressSpace = false) =>
            _conflicting.Contains(chord) || (chord == "Space" && !allowLongPressSpace)
                ? $"{chord} conflicts with a system shortcut" : null;
        public bool Clash(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);
    }

    [Fact]
    public void Initial_Step_Is_PickMic()
    {
        var vm = CreateViewModel();
        vm.Step.ShouldBe(OnboardingStep.PickMic);
    }

    [Fact]
    public void Cannot_Advance_From_PickMic_Until_Mic_Selected()
    {
        var vm = CreateViewModel();
        vm.CanAdvance.ShouldBeFalse();
        vm.SelectedMicDeviceId = "{abc-123}";
        vm.CanAdvance.ShouldBeTrue();
    }

    [Fact]
    public async Task Advance_From_PickMic_Goes_To_PickHotkeys()
    {
        var vm = CreateViewModel();
        vm.SelectedMicDeviceId = "{abc-123}";
        await vm.AdvanceAsync(TestContext.Current.CancellationToken);
        vm.Step.ShouldBe(OnboardingStep.PickHotkeys);
    }

    [Fact]
    public async Task Cannot_Advance_From_PickHotkeys_If_Conflict()
    {
        var vm = CreateViewModel(validator: new FakeValidator("Ctrl+C"));
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync(TestContext.Current.CancellationToken);
        vm.HoldHotkey = "Ctrl+C";
        vm.CanAdvance.ShouldBeFalse();
        vm.HoldHotkey = "RightCtrl+RightShift";
        vm.ToggleHotkey = "Ctrl+Shift+Space";
        vm.CanAdvance.ShouldBeTrue();
    }

    [Fact]
    public async Task Cannot_Advance_When_Default_Toggle_Chord_Is_Flagged_By_Validator()
    {
        var vm = CreateViewModel(validator: new FakeValidator("Ctrl+Shift+Space"));
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync(TestContext.Current.CancellationToken);
        vm.Step.ShouldBe(OnboardingStep.PickHotkeys);

        vm.ToggleHotkey.ShouldBe("Ctrl+Shift+Space");
        vm.ToggleHotkeyError.ShouldNotBeNull();
        vm.CanAdvance.ShouldBeFalse();

        vm.ToggleHotkey = "RightAlt+RightShift";
        vm.ToggleHotkeyError.ShouldBeNull();
        vm.CanAdvance.ShouldBeTrue();
    }

    [Fact]
    public async Task DownloadModels_AdvancesOnlyAfterVerifiedReadinessAndPipelineStart()
    {
        var provisioner = new FakeProvisioner();
        var pipelineStarts = 0;
        var vm = CreateViewModel(provisioner: provisioner, tryStartPipeline: () =>
        {
            pipelineStarts++;
            return true;
        });
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync(TestContext.Current.CancellationToken);
        await vm.AdvanceAsync(TestContext.Current.CancellationToken);
        await vm.AdvanceAsync(TestContext.Current.CancellationToken);
        provisioner.EnsureCalls.ShouldBe(1);
        provisioner.VerifyCalls.ShouldBe(1);
        pipelineStarts.ShouldBe(1);
        vm.Step.ShouldBe(OnboardingStep.TestDictation);
    }

    [Fact]
    public async Task DownloadFailure_StaysOnDownloadStep_AndOffersRetry()
    {
        var provisioner = new FakeProvisioner { EnsureError = new IOException("offline") };
        var vm = CreateViewModel(provisioner: provisioner);
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync(TestContext.Current.CancellationToken);
        await vm.AdvanceAsync(TestContext.Current.CancellationToken);
        await vm.AdvanceAsync(TestContext.Current.CancellationToken);

        vm.Step.ShouldBe(OnboardingStep.DownloadModels);
        vm.DownloadError!.ShouldContain("offline");
        vm.CanRetry.ShouldBeTrue();
    }

    // Ported from our (now-removed) OnboardingDownloadErrorTests: the Download
    // button doubles as Retry. After a failure a fresh Advance must clear the
    // inline error and, once provisioning succeeds, move to Test Dictation.
    [Fact]
    public async Task Retry_AfterFailureThenSuccess_ClearsErrorAndAdvances()
    {
        var provisioner = new FakeProvisioner { EnsureError = new IOException("first fails") };
        var vm = CreateViewModel(provisioner: provisioner);
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync(TestContext.Current.CancellationToken);
        await vm.AdvanceAsync(TestContext.Current.CancellationToken); // -> DownloadModels
        await vm.AdvanceAsync(TestContext.Current.CancellationToken); // fails

        vm.HasDownloadError.ShouldBeTrue();
        vm.Step.ShouldBe(OnboardingStep.DownloadModels);

        provisioner.EnsureError = null;                              // recover
        await vm.AdvanceAsync(TestContext.Current.CancellationToken); // retry succeeds

        vm.HasDownloadError.ShouldBeFalse();
        vm.Step.ShouldBe(OnboardingStep.TestDictation);
    }

    [Fact]
    public async Task DownloadModels_CannotSkipIntoTestDictation()
    {
        var vm = CreateViewModel();
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync(TestContext.Current.CancellationToken);
        await vm.AdvanceAsync(TestContext.Current.CancellationToken);

        vm.CanSkip.ShouldBeFalse();
        vm.Skip();
        vm.Step.ShouldBe(OnboardingStep.DownloadModels);
    }

    [Fact]
    public async Task FreshVerificationFailure_BlocksTestAndDoesNotStartPipeline()
    {
        var provisioner = new FakeProvisioner { VerificationResult = false };
        var pipelineStarts = 0;
        var vm = CreateViewModel(provisioner: provisioner, tryStartPipeline: () =>
        {
            pipelineStarts++;
            return true;
        });
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync(TestContext.Current.CancellationToken);
        await vm.AdvanceAsync(TestContext.Current.CancellationToken);

        await vm.AdvanceAsync(TestContext.Current.CancellationToken);

        vm.Step.ShouldBe(OnboardingStep.DownloadModels);
        vm.DownloadError.ShouldNotBeNull();
        pipelineStarts.ShouldBe(0);
    }

    [Fact]
    public async Task PipelineStartFailure_BlocksTestDictation()
    {
        var vm = CreateViewModel(tryStartPipeline: () => false);
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync(TestContext.Current.CancellationToken);
        await vm.AdvanceAsync(TestContext.Current.CancellationToken);

        await vm.AdvanceAsync(TestContext.Current.CancellationToken);

        vm.Step.ShouldBe(OnboardingStep.DownloadModels);
        vm.DownloadError!.ShouldContain("could not start");
    }

    [Fact]
    public async Task Finish_Sets_OnboardingCompleted()
    {
        var w = new FakeWriter();
        var vm = CreateViewModel(writer: w);
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync(TestContext.Current.CancellationToken);
        await vm.AdvanceAsync(TestContext.Current.CancellationToken);
        await vm.AdvanceAsync(TestContext.Current.CancellationToken);
        vm.TestDictationDone = true;
        await vm.AdvanceAsync(TestContext.Current.CancellationToken);
        vm.Step.ShouldBe(OnboardingStep.Done);
        w.Current.OnboardingCompleted.ShouldBeTrue();
    }

    // NOTE: our old skip-based tests (SkipAsync_Sets_OnboardingCompleted_And_Flushes,
    // Skip_From_DownloadModels_Advances_Without_Running_Stub,
    // DownloadModels_Step_Awaits_Stub_And_Advances) are intentionally dropped:
    // they are superseded by the upstream verified-provisioning requirement.
    // Skipping the model download can no longer complete onboarding
    // (DownloadModels_CannotSkipIntoTestDictation covers the new behavior), and
    // the stub downloader was replaced by IAsrProvisioningService. Our
    // hydration / skip-resolved-step behavior is retained below via InitializeFrom.

    [Fact]
    public async Task Advance_From_PickMic_Flushes_The_Checkpoint()
    {
        var w = new FakeWriter();
        var vm = CreateViewModel(writer: w);
        vm.SelectedMicDeviceId = "{mic-1}";

        await vm.AdvanceAsync(TestContext.Current.CancellationToken);

        w.Current.MicDeviceId.ShouldBe("{mic-1}");
        w.Flushes.ShouldBeGreaterThan(0);                       // checkpoint flushed
    }

    [Fact]
    public void InitializeFrom_NoMic_StartsAtPickMic()
    {
        var vm = CreateViewModel();
        vm.InitializeFrom(new AppSettings { MicDeviceId = "" },
            persistedMicPresent: false, modelsResolved: false);
        vm.Step.ShouldBe(OnboardingStep.PickMic);
    }

    [Fact]
    public void InitializeFrom_MicSetButMissingDevice_StartsAtPickMic()
    {
        var vm = CreateViewModel();
        vm.InitializeFrom(new AppSettings { MicDeviceId = "{gone}" },
            persistedMicPresent: false, modelsResolved: true);
        vm.Step.ShouldBe(OnboardingStep.PickMic);
    }

    [Fact]
    public void InitializeFrom_MicPresent_HotkeysValid_ModelsMissing_StartsAtDownloadModels()
    {
        var vm = CreateViewModel();
        vm.InitializeFrom(
            new AppSettings { MicDeviceId = "{mic}", HoldHotkey = "RightCtrl+RightShift", ToggleHotkey = "Ctrl+Shift+Space" },
            persistedMicPresent: true, modelsResolved: false);
        vm.Step.ShouldBe(OnboardingStep.DownloadModels);
    }

    [Fact]
    public void InitializeFrom_AllResolved_StartsAtTestDictation()
    {
        var vm = CreateViewModel();
        vm.InitializeFrom(
            new AppSettings { MicDeviceId = "{mic}", HoldHotkey = "RightCtrl+RightShift", ToggleHotkey = "Ctrl+Shift+Space" },
            persistedMicPresent: true, modelsResolved: true);
        vm.Step.ShouldBe(OnboardingStep.TestDictation);
    }

    [Fact]
    public void InitializeFrom_InvalidHotkey_StartsAtPickHotkeys()
    {
        // Validator flags the persisted toggle chord as conflicting.
        var vm = CreateViewModel(validator: new FakeValidator("Ctrl+Shift+Space"));
        vm.InitializeFrom(
            new AppSettings { MicDeviceId = "{mic}", HoldHotkey = "RightCtrl+RightShift", ToggleHotkey = "Ctrl+Shift+Space" },
            persistedMicPresent: true, modelsResolved: true);
        vm.Step.ShouldBe(OnboardingStep.PickHotkeys);
    }

    [Fact]
    public void InitializeFrom_Prefills_Mic_And_Hotkeys()
    {
        var vm = CreateViewModel();
        vm.InitializeFrom(
            new AppSettings { MicDeviceId = "{mic}", HoldHotkey = "LeftAlt+F9", ToggleHotkey = "LeftCtrl+LeftShift" },
            persistedMicPresent: true, modelsResolved: true);
        vm.SelectedMicDeviceId.ShouldBe("{mic}");
        vm.HoldHotkey.ShouldBe("LeftAlt+F9");
        vm.ToggleHotkey.ShouldBe("LeftCtrl+LeftShift");
    }

    private static OnboardingViewModel CreateViewModel(
        FakeWriter? writer = null,
        FakeProvisioner? provisioner = null,
        Func<bool>? tryStartPipeline = null,
        IHotkeyValidator? validator = null)
        => new(
            writer ?? new FakeWriter(),
            provisioner ?? new FakeProvisioner(),
            tryStartPipeline ?? (() => true),
            validator ?? new PermissiveValidator());
}
