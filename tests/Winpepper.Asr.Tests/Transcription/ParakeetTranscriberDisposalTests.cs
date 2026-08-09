using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests.Transcription;

public sealed class ParakeetTranscriberDisposalTests
{
    [Fact]
    public void ParakeetTranscriber_IsAnIDisposableTranscriber()
        => typeof(IDisposableTranscriber).IsAssignableFrom(typeof(ParakeetTranscriber)).ShouldBeTrue();

    // Behavior note: constructing a ParakeetSession needs real ONNX files, so
    // owned-session disposal is proven by the Windows-trait integration tests
    // and the type-level contract here. Dispose(ownsSession: false) must be a
    // no-op by construction — enforced by code review of the 10-line class.
}
