using Shouldly;
using Winpepper.Core.Settings;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

[Trait("Layer", "ViewModel")]
public class RecordingSettingsViewModelTests
{
    private sealed class FakeWriter : ISettingsWriter
    {
        public AppSettings Current { get; private set; } = new();
        public int WriteCount { get; private set; }
        public int FlushCount { get; private set; }
        public void Queue(Func<AppSettings, AppSettings> m) { Current = m(Current); WriteCount++; }
        public Task FlushAsync() { FlushCount++; return Task.CompletedTask; }
    }

    private sealed class FakeValidator : IHotkeyValidator
    {
        public string? Validate(string chord) => chord == "Ctrl+C" ? "Conflicts with Copy" : null;
        public bool Clash(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);
    }

    [Fact]
    public void Initial_Values_Come_From_AppSettings()
    {
        var s = new AppSettings { HoldHotkey = "Ctrl+Alt+D", MicDeviceId = "abc", PlaySounds = false };
        var vm = new RecordingSettingsViewModel(s, new FakeWriter());
        vm.HoldHotkey.ShouldBe("Ctrl+Alt+D");
        vm.MicDeviceId.ShouldBe("abc");
        vm.PlaySounds.ShouldBeFalse();
    }

    [Fact]
    public void Setting_HoldHotkey_Queues_Write()
    {
        var w = new FakeWriter();
        var vm = new RecordingSettingsViewModel(new AppSettings(), w);
        vm.HoldHotkey = "RightAlt+F12";
        w.WriteCount.ShouldBe(1);
        w.Current.HoldHotkey.ShouldBe("RightAlt+F12");
    }

    [Fact]
    public void Setting_HoldHotkey_To_Same_Value_Is_NoOp()
    {
        var w = new FakeWriter();
        var vm = new RecordingSettingsViewModel(new AppSettings { HoldHotkey = "Ctrl+Shift+Space" }, w);
        vm.HoldHotkey = "Ctrl+Shift+Space";
        w.WriteCount.ShouldBe(0);
    }

    [Fact]
    public void Conflicting_HoldHotkey_Sets_Conflict_Message()
    {
        var vm = new RecordingSettingsViewModel(new AppSettings(), new FakeWriter(), new FakeValidator());
        vm.HoldHotkey = "Ctrl+C";
        vm.HoldHotkeyConflict.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Same_Chord_For_Hold_And_Toggle_Surfaces_Conflict()
    {
        var vm = new RecordingSettingsViewModel(new AppSettings(), new FakeWriter(), new FakeValidator());
        vm.HoldHotkey = "RightCtrl+RightShift";
        vm.ToggleHotkey = "RightCtrl+RightShift";
        vm.ToggleHotkeyConflict.ShouldNotBeNull();
        vm.ToggleHotkeyConflict!.ShouldContain("Hold");
    }

    [Fact]
    public void Setting_PlaySounds_Raises_PropertyChanged()
    {
        var vm = new RecordingSettingsViewModel(new AppSettings(), new FakeWriter());
        var changes = new List<string>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? "");
        vm.PlaySounds = false;
        changes.ShouldContain(nameof(RecordingSettingsViewModel.PlaySounds));
    }

    [Fact]
    public void Setting_SpeakerFilter_Queues_And_Flushes_Durably()
    {
        var w = new FakeWriter();
        var vm = new RecordingSettingsViewModel(new AppSettings(), w);

        vm.SpeakerFilterEnabled = true;

        w.Current.SpeakerFilterEnabled.ShouldBeTrue();
        w.WriteCount.ShouldBe(1);   // exactly one write per real change
        w.FlushCount.ShouldBe(1);   // and it was flushed durably
    }
}
