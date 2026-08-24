using Shouldly;
using Winpepper.Core.Settings;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public sealed class OnboardingModelPickerTests
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

    private static (OnboardingViewModel Vm, FakeProvisioner Prov, FakeWriter Writer, List<bool> PipelineStarts)
        CreateAtDownloadStep(bool pipelineStartResult = true)
    {
        var prov = new FakeProvisioner();
        var writer = new FakeWriter();
        var starts = new List<bool>();
        var vm = new OnboardingViewModel(writer, () => { starts.Add(pipelineStartResult); return pipelineStartResult; },
            new PermissiveValidator(), prov, Catalog);
        vm.SelectedMicDeviceId = "mic-1";
        vm.AdvanceAsync().GetAwaiter().GetResult();  // PickMic -> PickHotkeys
        vm.AdvanceAsync().GetAwaiter().GetResult();  // PickHotkeys -> DownloadModels
        return (vm, prov, writer, starts);
    }

    [Fact]
    public void Defaults_EnglishSelected_OptionsUnchecked_TotalIsSpeechOnly()
    {
        var (vm, _, _, _) = CreateAtDownloadStep();
        vm.MultilingualSelected.ShouldBeFalse();
        vm.BackupModelSelected.ShouldBeFalse();
        vm.CleanupModelSelected.ShouldBeFalse();
        vm.TotalDownloadText.ShouldBe("Total download: 756 MB");
    }

    [Theory]
    [InlineData(false, false, false, "Total download: 756 MB")]
    [InlineData(false, true,  false, "Total download: 1.4 GB")]   // 756+670=1426
    [InlineData(false, false, true,  "Total download: 1.2 GB")]   // 756+491=1247
    [InlineData(false, true,  true,  "Total download: 1.9 GB")]   // 1917
    [InlineData(true,  false, false, "Total download: 777 MB")]
    [InlineData(true,  true,  true,  "Total download: 1.9 GB")]   // 777+670+491=1938
    public void TotalDownload_SumsSelectedItems(bool multi, bool backup, bool cleanup, string expected)
    {
        var (vm, _, _, _) = CreateAtDownloadStep();
        vm.MultilingualSelected = multi;
        vm.BackupModelSelected = backup;
        vm.CleanupModelSelected = cleanup;
        vm.TotalDownloadText.ShouldBe(expected);
    }

    [Fact]
    public void SizeLabels_RoundToNearestTenWithTilde()
    {
        OnboardingViewModel.SizeLabel(755_608_086).ShouldBe("~760 MB");
        OnboardingViewModel.SizeLabel(777_052_150).ShouldBe("~780 MB");
        OnboardingViewModel.SizeLabel(670_479_942).ShouldBe("~670 MB");
        OnboardingViewModel.SizeLabel(491_400_032).ShouldBe("~490 MB");
    }

    [Fact]
    public async Task Advance_PersistsChoices_StartsBackgroundDownloads_AndMovesToTestDictation()
    {
        var (vm, prov, writer, _) = CreateAtDownloadStep();
        vm.MultilingualSelected = true;
        vm.BackupModelSelected = true;

        await vm.AdvanceAsync();

        vm.Step.ShouldBe(OnboardingStep.TestDictation);      // advance is IMMEDIATE (background download)
        prov.Starts.Count.ShouldBe(1);
        prov.Starts[0].Speech.ShouldBe("nemotron-streaming-multi");
        prov.Starts[0].Names.ShouldBe(new[] { "nemotron-streaming-multi", "parakeet-tdt-0.6b-v3" });
        var s = writer.Applied(new AppSettings());           // FakeWriter applies queued mutators
        s.StreamingModelName.ShouldBe("nemotron-streaming-multi");
        s.OnboardingBackupModelChosen.ShouldBeTrue();
        s.OnboardingCleanupModelChosen.ShouldBeFalse();
    }

    [Fact]
    public async Task Advance_CleanupModelOptedIn_TurnsCleanupEnabledOn()
    {
        // 2026-08-24: cleanup is opt-in by default. Choosing the cleanup MODEL
        // in onboarding is the opt-in gesture — it must also switch the feature
        // on, otherwise on-first-use users would download a model that never runs.
        var (vm, prov, writer, _) = CreateAtDownloadStep();
        vm.CleanupModelSelected = true;

        await vm.AdvanceAsync();

        prov.Starts[0].Names.ShouldContain("qwen2.5-0.5b-instruct-q4_k_m");
        var s = writer.Applied(new AppSettings());
        s.OnboardingCleanupModelChosen.ShouldBeTrue();
        s.CleanupEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Advance_CleanupModelNotChosen_LeavesCleanupEnabledAsPersisted()
    {
        var (vm, _, writer, _) = CreateAtDownloadStep();

        await vm.AdvanceAsync();

        // Fresh default stays opt-in-off...
        writer.Applied(new AppSettings()).CleanupEnabled.ShouldBeFalse();
        // ...and an earlier explicit opt-in is never stripped by onboarding.
        writer.Applied(new AppSettings { CleanupEnabled = true }).CleanupEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task TestDictation_GatesOnSpeechVerifiedAndPipelineStart()
    {
        var (vm, prov, _, pipelineStarts) = CreateAtDownloadStep();
        await vm.AdvanceAsync();                              // -> TestDictation, downloads running
        vm.TestDictationDone = true;
        vm.CanAdvance.ShouldBeFalse();                        // model not verified yet
        pipelineStarts.ShouldBeEmpty();

        prov.Publish(new OnboardingDownloadState(100, "All models verified — ready to dictate.", null, SpeechModelReady: true));

        vm.SpeechModelVerified.ShouldBeTrue();
        pipelineStarts.Count.ShouldBe(1);
        vm.CanAdvance.ShouldBeTrue();

        await vm.AdvanceAsync();                              // Finish
        vm.Step.ShouldBe(OnboardingStep.Done);
    }

    [Fact]
    public async Task PipelineStartFailure_BlocksFinish_AndRetryReruns()
    {
        var (vm, prov, _, _) = CreateAtDownloadStep(pipelineStartResult: false);
        await vm.AdvanceAsync();
        prov.Publish(new OnboardingDownloadState(100, "ready", null, SpeechModelReady: true));

        vm.SpeechModelVerified.ShouldBeFalse();
        vm.DownloadError.ShouldNotBeNull();
        vm.TestDictationDone = true;
        vm.CanAdvance.ShouldBeFalse();
        vm.CanRetry.ShouldBeTrue();

        vm.RetryDownloads();
        prov.Starts.Count.ShouldBe(2);
    }

    [Fact]
    public void Resume_AtTestDictation_RehydratesSelection_AndRestartsDownloadsForVerification()
    {
        var prov = new FakeProvisioner();
        var vm = new OnboardingViewModel(new FakeWriter(), () => true, new PermissiveValidator(), prov, Catalog);
        var settings = new AppSettings
        {
            MicDeviceId = "mic-1",
            StreamingModelName = "nemotron-streaming-multi",
            OnboardingBackupModelChosen = true,
        };
        vm.InitializeFrom(settings, persistedMicPresent: true, modelsResolved: true);
        vm.Step.ShouldBe(OnboardingStep.TestDictation);
        vm.MultilingualSelected.ShouldBeTrue();
        prov.Starts.Count.ShouldBe(1); // verification/resume kick
        prov.Starts[0].Names.ShouldBe(new[] { "nemotron-streaming-multi", "parakeet-tdt-0.6b-v3" });
    }
}
