using System.Collections.Specialized;
using Microsoft.Extensions.Logging;
using Shouldly;
using Winpepper.Core.Logging;
using Winpepper.Core.Threading;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public class DiagnosticsViewModelTests
{
    private static DiagnosticsViewModel Build(LogRingBuffer buf, FakeHost host)
        => new(buf, new SynchronousUiThread(), host);

    [Fact]
    public void Existing_Buffer_Entries_Are_Hydrated_On_Construct()
    {
        var buf = new LogRingBuffer(capacity: 5);
        buf.Append(new LogTailEntry(DateTime.UtcNow, "INF", "boot"));
        var host = new FakeHost();

        var vm = Build(buf, host);

        vm.Tail.Count.ShouldBe(1);
        vm.Tail[0].Message.ShouldBe("boot");
    }

    [Fact]
    public void New_Appends_Flow_Into_Tail()
    {
        var buf = new LogRingBuffer(capacity: 5);
        var host = new FakeHost();
        var vm = Build(buf, host);

        buf.Append(new LogTailEntry(DateTime.UtcNow, "WRN", "uh oh"));

        vm.Tail.Count.ShouldBe(1);
        vm.Tail[0].Level.ShouldBe("WRN");
    }

    [Fact]
    public async Task CopyDiagnosticsBundle_Invokes_Host_And_Sets_LastBundlePath()
    {
        var buf = new LogRingBuffer(capacity: 5);
        var host = new FakeHost { ReturnedBundlePath = "C:\\temp\\bundle.zip" };
        var vm = Build(buf, host);

        await vm.CopyDiagnosticsBundleAsync();

        host.SaveBundleCalled.ShouldBeTrue();
        vm.LastBundlePath.ShouldBe("C:\\temp\\bundle.zip");
    }

    [Fact]
    public void OpenLogFolder_Invokes_Host()
    {
        var buf = new LogRingBuffer(capacity: 5);
        var host = new FakeHost();
        var vm = Build(buf, host);

        vm.OpenLogFolder();

        host.OpenLogFolderCalled.ShouldBeTrue();
    }

    [Fact]
    public void Tail_Is_Capped_At_Buffer_Capacity()
    {
        var buf = new LogRingBuffer(capacity: 3);
        var host = new FakeHost();
        var vm = Build(buf, host);

        for (var i = 0; i < 10; i++) buf.Append(new LogTailEntry(DateTime.UtcNow, "INF", $"l{i}"));

        vm.Tail.Count.ShouldBe(3);
        vm.Tail[0].Message.ShouldBe("l7");
        vm.Tail[2].Message.ShouldBe("l9");
    }

    private sealed class FakeHost : IDiagnosticsHost
    {
        public bool OpenLogFolderCalled { get; private set; }
        public bool SaveBundleCalled { get; private set; }
        public string? ReturnedBundlePath { get; set; }

        public void OpenLogFolder() => OpenLogFolderCalled = true;
        public Task<string?> SaveBundleAsync()
        {
            SaveBundleCalled = true;
            return Task.FromResult(ReturnedBundlePath);
        }
    }
}
