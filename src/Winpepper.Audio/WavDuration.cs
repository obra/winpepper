namespace Winpepper.Audio;

/// <summary>
/// Header-only, NON-THROWING WAV duration probe. Used once per process to
/// measure the start-cue asset (Assets/start.wav) so the silence gate can mask
/// the cue's contamination window without hardcoding the cue length (owner
/// requirement: the sound file may change or become user-configurable).
///
/// Duration semantics: exact <c>sampleFrames * 1000 / sampleRate</c> from the
/// fmt + data chunks (NOT the whole-20 ms-frame duration SilenceTrimmer uses,
/// and NOT the history index's recorded duration).
///
/// This is deliberately a FIFTH hand-rolled RIFF reader (see
/// Winpepper.History.WavWriter, Winpepper.Asr PcmWavEncoder, the bench's
/// BenchAudio, and the Asr test helpers): all four existing readers THROW on
/// malformed or non-16 kHz input and decode the sample data. This one must do
/// neither — the shipped cue is 22050 Hz, only the header matters, and a failed
/// measurement must FAIL OPEN (return false ⇒ cue mask 0 ⇒ the gate behaves
/// exactly as it did before the mask existed). BCL-only, no NAudio, per the
/// repo's cross-platform policy (WavWriter.cs:5-6).
/// </summary>
public static class WavDuration
{
    /// <summary>
    /// Measure a WAV file's duration in milliseconds from its header.
    /// Returns false (durationMs 0) for: missing/unreadable file, zero-length
    /// file, non-RIFF/WAVE bytes, truncated header or data chunk (claimed
    /// chunk size exceeds the bytes actually present), non-PCM format tag,
    /// missing fmt/data chunk, or zero sample-rate/block-align.
    /// A structurally valid zero-length data chunk returns TRUE with 0 ms.
    /// </summary>
    public static bool TryMeasureMs(string path, out int durationMs)
    {
        durationMs = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var r = new BinaryReader(fs);

            if (fs.Length < 12) return false;
            if (ReadFourCc(r) != "RIFF") return false;
            r.ReadInt32(); // riff chunk size — untrusted, ignored
            if (ReadFourCc(r) != "WAVE") return false;

            short formatTag = 0, blockAlign = 0;
            var sampleRate = 0;
            long dataBytes = -1;
            var haveFmt = false;

            while (fs.Position + 8 <= fs.Length)
            {
                var id = ReadFourCc(r);
                long size = r.ReadUInt32();
                // Every chunk's payload must actually be present — a claimed
                // size past EOF means truncation and the header cannot be
                // trusted (fail open).
                if (fs.Position + size > fs.Length) return false;

                if (id == "fmt ")
                {
                    if (size < 16) return false;
                    var fmtStart = fs.Position;
                    formatTag = r.ReadInt16();
                    r.ReadInt16(); // channels — folded into blockAlign
                    sampleRate = r.ReadInt32();
                    r.ReadInt32(); // byte rate — derivable, ignored
                    blockAlign = r.ReadInt16();
                    r.ReadInt16(); // bits per sample — folded into blockAlign
                    haveFmt = true;
                    fs.Position = fmtStart + size + (size & 1); // odd-size pad
                }
                else if (id == "data")
                {
                    dataBytes = size;
                    fs.Position += size + (size & 1);
                }
                else
                {
                    fs.Position += size + (size & 1);
                }
            }

            if (!haveFmt || dataBytes < 0) return false;
            if (formatTag != 1) return false; // PCM only — anything exotic fails open
            if (sampleRate <= 0 || blockAlign <= 0) return false;

            var frames = dataBytes / blockAlign;
            durationMs = (int)(frames * 1000 / sampleRate);
            return true;
        }
        catch (Exception)
        {
            // Fail-open probe: ANY I/O or parse surprise means "no measured
            // cue" (mask 0). The caller logs the condition; see PipelineHost.
            durationMs = 0;
            return false;
        }
    }

    private static string ReadFourCc(BinaryReader r)
    {
        var b = r.ReadBytes(4);
        return b.Length == 4 ? System.Text.Encoding.ASCII.GetString(b) : "";
    }
}
