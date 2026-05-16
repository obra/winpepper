using Shouldly;
using Xunit;

namespace Winpepper.Models.Tests;

public class DownloadProgressTests
{
    [Fact]
    public void PercentComplete_IsBytesOverTotal_Times100()
    {
        var p = new DownloadProgress
        {
            DescriptorName = "x",
            FileRelativePath = "a.bin",
            BytesDownloaded = 250,
            TotalBytes = 1000,
            Phase = DownloadPhase.Downloading,
        };
        p.PercentComplete.ShouldBe(25.0, 0.001);
    }

    [Fact]
    public void PercentComplete_ZeroTotal_ReturnsZero()
    {
        var p = new DownloadProgress
        {
            DescriptorName = "x",
            FileRelativePath = "a.bin",
            BytesDownloaded = 0,
            TotalBytes = 0,
            Phase = DownloadPhase.Downloading,
        };
        p.PercentComplete.ShouldBe(0.0);
    }

    [Fact]
    public void Phases_AreOrdered()
    {
        ((int)DownloadPhase.Pending).ShouldBeLessThan((int)DownloadPhase.Downloading);
        ((int)DownloadPhase.Downloading).ShouldBeLessThan((int)DownloadPhase.Verifying);
        ((int)DownloadPhase.Verifying).ShouldBeLessThan((int)DownloadPhase.Complete);
    }
}
