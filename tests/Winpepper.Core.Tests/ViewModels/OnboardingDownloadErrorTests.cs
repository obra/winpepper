using Shouldly;
using Winpepper.Core.Settings;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public class OnboardingDownloadErrorTests
{
    private sealed class FakeWriter : ISettingsWriter
    {
        public void Queue(System.Func<AppSettings, AppSettings> mutator) { }
        public System.Threading.Tasks.Task FlushAsync() => System.Threading.Tasks.Task.CompletedTask;
    }

    private sealed class PermissiveValidator : IHotkeyValidator
    {
        public string? Validate(string chord) => null;
        public bool Clash(string a, string b) => false;
    }

    private static OnboardingViewModel AtDownloadStep(System.Func<System.Threading.Tasks.Task> downloader,
                                                      System.Action<System.Exception>? onErr = null)
    {
        var vm = new OnboardingViewModel(new FakeWriter(), downloader, new PermissiveValidator(), onErr);
        // Jump straight to the download step: models unresolved, mic+hotkeys resolved.
        vm.InitializeFrom(new AppSettings { MicDeviceId = "mic-1" }, persistedMicPresent: true, modelsResolved: false);
        vm.Step.ShouldBe(OnboardingStep.DownloadModels);
        return vm;
    }

    [Fact]
    public async System.Threading.Tasks.Task Advance_DownloadFailure_DoesNotThrow_StaysOnStep_ShowsError()
    {
        var called = false;
        var vm = AtDownloadStep(
            downloader: () => throw new System.Net.Http.HttpRequestException("network down"),
            onErr: _ => called = true);

        await vm.AdvanceAsync(); // must NOT throw

        vm.Step.ShouldBe(OnboardingStep.DownloadModels);
        vm.HasDownloadError.ShouldBeTrue();
        vm.DownloadError.ShouldNotBeNullOrWhiteSpace();
        vm.CanSkip.ShouldBeTrue();   // Skip stays usable
        called.ShouldBeTrue();        // failure was logged
    }

    [Fact]
    public async System.Threading.Tasks.Task Advance_DownloadSuccess_AdvancesAndNoError()
    {
        var vm = AtDownloadStep(downloader: () => System.Threading.Tasks.Task.CompletedTask);

        await vm.AdvanceAsync();

        vm.Step.ShouldBe(OnboardingStep.TestDictation);
        vm.HasDownloadError.ShouldBeFalse();
        vm.DownloadError.ShouldBeNull();
    }

    [Fact]
    public async System.Threading.Tasks.Task Retry_AfterFailureThenSuccess_ClearsErrorAndAdvances()
    {
        var attempt = 0;
        var vm = AtDownloadStep(downloader: () =>
        {
            attempt++;
            if (attempt == 1) throw new System.Net.Http.HttpRequestException("first fails");
            return System.Threading.Tasks.Task.CompletedTask;
        });

        await vm.AdvanceAsync(); // fails
        vm.HasDownloadError.ShouldBeTrue();
        vm.Step.ShouldBe(OnboardingStep.DownloadModels);

        await vm.AdvanceAsync(); // retry succeeds
        vm.HasDownloadError.ShouldBeFalse();
        vm.Step.ShouldBe(OnboardingStep.TestDictation);
    }
}
