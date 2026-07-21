using Shouldly;
using Winpepper.Core.Settings;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

[Trait("Layer", "ViewModel")]
public class OnboardingViewModelTests
{
    private sealed class FakeWriter : ISettingsWriter
    {
        public AppSettings Current = new();
        public int Flushes;
        public void Queue(Func<AppSettings, AppSettings> m) => Current = m(Current);
        public Task FlushAsync() { Flushes++; return Task.CompletedTask; }
    }

    private sealed class PermissiveValidator : IHotkeyValidator
    {
        public string? Validate(string chord) => null;
        public bool Clash(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);
    }

    private sealed class FakeValidator : IHotkeyValidator
    {
        private readonly HashSet<string> _conflicting;
        public FakeValidator(params string[] conflicting) =>
            _conflicting = new HashSet<string>(conflicting, StringComparer.Ordinal);
        public string? Validate(string chord) =>
            _conflicting.Contains(chord) ? $"{chord} conflicts with a system shortcut" : null;
        public bool Clash(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);
    }

    [Fact]
    public void Initial_Step_Is_PickMic()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask, new PermissiveValidator());
        vm.Step.ShouldBe(OnboardingStep.PickMic);
    }

    [Fact]
    public void Cannot_Advance_From_PickMic_Until_Mic_Selected()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask, new PermissiveValidator());
        vm.CanAdvance.ShouldBeFalse();
        vm.SelectedMicDeviceId = "{abc-123}";
        vm.CanAdvance.ShouldBeTrue();
    }

    [Fact]
    public async Task Advance_From_PickMic_Goes_To_PickHotkeys()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask, new PermissiveValidator());
        vm.SelectedMicDeviceId = "{abc-123}";
        await vm.AdvanceAsync();
        vm.Step.ShouldBe(OnboardingStep.PickHotkeys);
    }

    [Fact]
    public async Task Cannot_Advance_From_PickHotkeys_If_Conflict()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask, new FakeValidator("Ctrl+C"));
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync();
        vm.HoldHotkey = "Ctrl+C";
        vm.CanAdvance.ShouldBeFalse();
        vm.HoldHotkey = "RightCtrl+RightShift";
        vm.ToggleHotkey = "Ctrl+Shift+Space";
        vm.CanAdvance.ShouldBeTrue();
    }

    [Fact]
    public async Task Cannot_Advance_When_Default_Toggle_Chord_Is_Flagged_By_Validator()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask,
            new FakeValidator("Ctrl+Shift+Space"));
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync();
        vm.Step.ShouldBe(OnboardingStep.PickHotkeys);

        vm.ToggleHotkey.ShouldBe("Ctrl+Shift+Space");
        vm.ToggleHotkeyError.ShouldNotBeNull();
        vm.CanAdvance.ShouldBeFalse();

        vm.ToggleHotkey = "RightAlt+RightShift";
        vm.ToggleHotkeyError.ShouldBeNull();
        vm.CanAdvance.ShouldBeTrue();
    }

    [Fact]
    public async Task DownloadModels_Step_Awaits_Stub_And_Advances()
    {
        var downloaded = false;
        var vm = new OnboardingViewModel(new FakeWriter(),
            () => { downloaded = true; return Task.CompletedTask; },
            new PermissiveValidator());
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync();
        await vm.AdvanceAsync();
        await vm.AdvanceAsync();
        downloaded.ShouldBeTrue();
        vm.Step.ShouldBe(OnboardingStep.TestDictation);
    }

    [Fact]
    public async Task Skip_From_DownloadModels_Advances_Without_Running_Stub()
    {
        var downloaded = false;
        var vm = new OnboardingViewModel(new FakeWriter(),
            () => { downloaded = true; return Task.CompletedTask; },
            new PermissiveValidator());
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync();
        await vm.AdvanceAsync();
        await vm.SkipAsync();
        downloaded.ShouldBeFalse();
        vm.Step.ShouldBe(OnboardingStep.TestDictation);
    }

    [Fact]
    public async Task Finish_Sets_OnboardingCompleted()
    {
        var w = new FakeWriter();
        var vm = new OnboardingViewModel(w, () => Task.CompletedTask, new PermissiveValidator());
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync();
        await vm.AdvanceAsync(); await vm.SkipAsync();
        vm.TestDictationDone = true;
        await vm.AdvanceAsync();
        vm.Step.ShouldBe(OnboardingStep.Done);
        w.Current.OnboardingCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task SkipAsync_Sets_OnboardingCompleted_And_Flushes()
    {
        var w = new FakeWriter();
        var vm = new OnboardingViewModel(w, () => Task.CompletedTask, new PermissiveValidator());
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync();  // -> PickHotkeys
        await vm.AdvanceAsync();                                 // -> DownloadModels
        var flushesBefore = w.Flushes;

        await vm.SkipAsync();

        w.Current.OnboardingCompleted.ShouldBeTrue();
        w.Flushes.ShouldBeGreaterThan(flushesBefore);           // Skip flushed
        vm.Step.ShouldBe(OnboardingStep.TestDictation);
    }

    [Fact]
    public async Task Advance_From_PickMic_Flushes_The_Checkpoint()
    {
        var w = new FakeWriter();
        var vm = new OnboardingViewModel(w, () => Task.CompletedTask, new PermissiveValidator());
        vm.SelectedMicDeviceId = "{mic-1}";

        await vm.AdvanceAsync();

        w.Current.MicDeviceId.ShouldBe("{mic-1}");
        w.Flushes.ShouldBeGreaterThan(0);                       // checkpoint flushed
    }

    [Fact]
    public void InitializeFrom_NoMic_StartsAtPickMic()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask, new PermissiveValidator());
        vm.InitializeFrom(new AppSettings { MicDeviceId = "" },
            persistedMicPresent: false, modelsResolved: false);
        vm.Step.ShouldBe(OnboardingStep.PickMic);
    }

    [Fact]
    public void InitializeFrom_MicSetButMissingDevice_StartsAtPickMic()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask, new PermissiveValidator());
        vm.InitializeFrom(new AppSettings { MicDeviceId = "{gone}" },
            persistedMicPresent: false, modelsResolved: true);
        vm.Step.ShouldBe(OnboardingStep.PickMic);
    }

    [Fact]
    public void InitializeFrom_MicPresent_HotkeysValid_ModelsMissing_StartsAtDownloadModels()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask, new PermissiveValidator());
        vm.InitializeFrom(
            new AppSettings { MicDeviceId = "{mic}", HoldHotkey = "RightCtrl+RightShift", ToggleHotkey = "Ctrl+Shift+Space" },
            persistedMicPresent: true, modelsResolved: false);
        vm.Step.ShouldBe(OnboardingStep.DownloadModels);
    }

    [Fact]
    public void InitializeFrom_AllResolved_StartsAtTestDictation()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask, new PermissiveValidator());
        vm.InitializeFrom(
            new AppSettings { MicDeviceId = "{mic}", HoldHotkey = "RightCtrl+RightShift", ToggleHotkey = "Ctrl+Shift+Space" },
            persistedMicPresent: true, modelsResolved: true);
        vm.Step.ShouldBe(OnboardingStep.TestDictation);
    }

    [Fact]
    public void InitializeFrom_InvalidHotkey_StartsAtPickHotkeys()
    {
        // Validator flags the persisted toggle chord as conflicting.
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask,
            new FakeValidator("Ctrl+Shift+Space"));
        vm.InitializeFrom(
            new AppSettings { MicDeviceId = "{mic}", HoldHotkey = "RightCtrl+RightShift", ToggleHotkey = "Ctrl+Shift+Space" },
            persistedMicPresent: true, modelsResolved: true);
        vm.Step.ShouldBe(OnboardingStep.PickHotkeys);
    }

    [Fact]
    public void InitializeFrom_Prefills_Mic_And_Hotkeys()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask, new PermissiveValidator());
        vm.InitializeFrom(
            new AppSettings { MicDeviceId = "{mic}", HoldHotkey = "LeftAlt+F9", ToggleHotkey = "LeftCtrl+LeftShift" },
            persistedMicPresent: true, modelsResolved: true);
        vm.SelectedMicDeviceId.ShouldBe("{mic}");
        vm.HoldHotkey.ShouldBe("LeftAlt+F9");
        vm.ToggleHotkey.ShouldBe("LeftCtrl+LeftShift");
    }
}
