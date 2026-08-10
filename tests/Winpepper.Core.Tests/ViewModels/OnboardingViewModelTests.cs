using Shouldly;
using Winpepper.Core.Settings;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

/// <summary>Shared by <see cref="OnboardingViewModelTests"/> and
/// <see cref="OnboardingModelPickerTests"/>. Captures queued mutators so tests
/// can fold them over a seed via <see cref="Applied"/>.</summary>
internal sealed class FakeWriter : ISettingsWriter
{
    public AppSettings Current = new();
    public int Flushes;
    private readonly List<Func<AppSettings, AppSettings>> _mutators = new();

    public void Queue(Func<AppSettings, AppSettings> m)
    {
        _mutators.Add(m);
        Current = m(Current);
    }

    public Task FlushAsync() { Flushes++; return Task.CompletedTask; }

    /// <summary>Folds every captured mutator over <paramref name="seed"/>.</summary>
    public AppSettings Applied(AppSettings seed)
    {
        var s = seed;
        foreach (var m in _mutators) s = m(s);
        return s;
    }
}

internal sealed class PermissiveValidator : IHotkeyValidator
{
    public string? Validate(string chord, bool allowLongPressSpace = false) => null;
    public bool Clash(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);
}

[Trait("Layer", "ViewModel")]
public class OnboardingViewModelTests
{
    private static readonly ModelPickerCatalog Catalog = new(
        EnglishName: "nemotron-streaming-en", EnglishBytes: 755_608_086,
        MultilingualName: "nemotron-streaming-multi", MultilingualBytes: 777_052_150,
        BackupName: "parakeet-tdt-0.6b-v3", BackupBytes: 670_479_942,
        CleanupName: "qwen2.5-0.5b-instruct-q4_k_m", CleanupBytes: 491_400_032);

    private sealed class FakeProvisioner : IOnboardingModelProvisioner
    {
        public OnboardingDownloadState State { get; private set; } =
            new(0, "Waiting", null, SpeechModelReady: false);
        public event EventHandler<OnboardingDownloadState>? StateChanged;
        public List<(IReadOnlyList<string> Names, string Speech)> Starts { get; } = new();
        public void StartDownloads(IReadOnlyList<string> modelNames, string speechModelName)
            => Starts.Add((modelNames, speechModelName));
        public void Publish(OnboardingDownloadState s) { State = s; StateChanged?.Invoke(this, s); }
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

    // NOTE: the old DownloadModels-behavior tests (advance-after-verify,
    // download-failure/retry, verify-false, pipeline-false, cannot-skip) are
    // superseded by OnboardingModelPickerTests: downloads now start in the
    // BACKGROUND when leaving the Download step and Test Dictation gates on
    // SpeechModelVerified + pipeline start instead.

    [Fact]
    public async Task Finish_Sets_OnboardingCompleted()
    {
        var w = new FakeWriter();
        var provisioner = new FakeProvisioner();
        var vm = CreateViewModel(writer: w, provisioner: provisioner);
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync(TestContext.Current.CancellationToken);
        await vm.AdvanceAsync(TestContext.Current.CancellationToken); // PickHotkeys -> DownloadModels
        await vm.AdvanceAsync(TestContext.Current.CancellationToken); // DownloadModels -> TestDictation (background downloads)
        provisioner.Publish(new OnboardingDownloadState(100, "ready", null, SpeechModelReady: true));
        vm.TestDictationDone = true;
        await vm.AdvanceAsync(TestContext.Current.CancellationToken);
        vm.Step.ShouldBe(OnboardingStep.Done);
        w.Current.OnboardingCompleted.ShouldBeTrue();
    }

    // NOTE: our old skip-based tests (SkipAsync_Sets_OnboardingCompleted_And_Flushes,
    // Skip_From_DownloadModels_Advances_Without_Running_Stub,
    // DownloadModels_Step_Awaits_Stub_And_Advances) are intentionally dropped:
    // they are superseded by the upstream verified-provisioning requirement.
    // Skipping the model download can no longer complete onboarding, and the
    // stub downloader was replaced by IOnboardingModelProvisioner. Our
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
            tryStartPipeline ?? (() => true),
            validator ?? new PermissiveValidator(),
            provisioner ?? new FakeProvisioner(),
            Catalog);
}
