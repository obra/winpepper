using Shouldly;
using Winpepper.Core.Audio;
using Xunit;

namespace Winpepper.Core.Tests.Audio;

public class NoopSoundEffectPlayerTests
{
    [Fact]
    public void Calls_Are_Counted()
    {
        var p = new NoopSoundEffectPlayer();
        p.PlayStart();
        p.PlayStop();
        p.PlayStart();
        p.StartPlays.ShouldBe(2);
        p.StopPlays.ShouldBe(1);
    }
}
