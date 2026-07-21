using Shouldly;
using Winpepper.Asr.Transcription;
using Winpepper.Corrections;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class CorrectionSpellingMapperTests
{
    [Fact]
    public void Replacements_MapToCustomSpelling_KeytermsOffByDefault()
    {
        var data = CorrectionsData.Empty with
        {
            Replacements = new Dictionary<string, string> { ["kubernettes"] = "Kubernetes", ["winpeper"] = "Winpepper" },
            Preferred = new[] { "Amplifier" },
        };

        var extras = CorrectionSpellingMapper.ToExtras(data, includeKeyterms: false);

        extras.CustomSpelling.Count.ShouldBe(2);
        extras.CustomSpelling.ShouldContain(cs => cs.To == "Kubernetes" && cs.From.Count == 1 && cs.From[0] == "kubernettes");
        extras.CustomSpelling.ShouldContain(cs => cs.To == "Winpepper" && cs.From[0] == "winpeper");
        extras.Keyterms.ShouldBeEmpty(); // opt-in
    }

    [Fact]
    public void Keyterms_IncludedWhenEnabled()
    {
        var data = CorrectionsData.Empty with { Preferred = new[] { "Amplifier", "Winpepper" } };
        var extras = CorrectionSpellingMapper.ToExtras(data, includeKeyterms: true);
        extras.Keyterms.ShouldBe(new[] { "Amplifier", "Winpepper" });
    }

    [Fact]
    public void Empty_YieldsEmptyExtras()
    {
        var extras = CorrectionSpellingMapper.ToExtras(CorrectionsData.Empty, includeKeyterms: true);
        extras.CustomSpelling.ShouldBeEmpty();
        extras.Keyterms.ShouldBeEmpty();
    }
}
