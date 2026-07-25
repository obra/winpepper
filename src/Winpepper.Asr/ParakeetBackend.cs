namespace Winpepper.Asr;

/// <summary>Encoder output laid out [Dim, Frames] row-major: Data[d * Frames + t].</summary>
public readonly record struct EncoderOutput(float[] Data, int ValidLen, int Dim, int Frames);

/// <summary>One decoder_joint step's outputs (fresh arrays each call).</summary>
public sealed record DecoderJointResult(float[] Logits, float[] StateH, float[] StateC);

/// <summary>
/// Seam over the two ONNX models so the greedy TDT decode loop and the chunked
/// streaming session are pure and Linux-testable. Implemented by ParakeetSession.
/// </summary>
public interface IParakeetBackend
{
    int VocabSize { get; }
    int BlankId { get; }
    int DecoderHiddenLayers { get; }
    int DecoderHiddenDim { get; }

    /// <summary>Run the encoder over [T, FeatureSize] normalized mel frames.</summary>
    EncoderOutput Encode(float[,] melFrames);

    /// <summary>Run one decoder_joint step for a single encoder frame (length Dim).</summary>
    DecoderJointResult DecodeJoint(float[] encoderFrame, int lastToken, float[] stateH, float[] stateC);

    string DecodeTokens(IEnumerable<int> tokenIds);
}
