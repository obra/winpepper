using System;
using System.IO;

namespace AsrLatencyBench;

/// <summary>
/// Pure audio helpers for the bench: WAV loading (16 kHz mono int16 only),
/// deterministic gain / leading-silence transforms, and RMS stats used to
/// pick a --gain that keeps the quiet-talker guard active. BCL-only so the
/// same file compiles into Winpepper.Asr.Tests via Compile Include.
/// </summary>
public static class BenchAudio
{
    public static float[] ReadMono16k(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        if (new string(br.ReadChars(4)) != "RIFF")
            throw new InvalidDataException($"{path}: not a RIFF file");
        br.ReadInt32(); // riff chunk size
        if (new string(br.ReadChars(4)) != "WAVE")
            throw new InvalidDataException($"{path}: not a WAVE file");

        short channels = 0, bits = 0;
        var rate = 0;
        byte[]? data = null;
        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            var id = new string(br.ReadChars(4));
            var size = br.ReadInt32();
            if (id == "fmt ")
            {
                br.ReadInt16(); // format tag
                channels = br.ReadInt16();
                rate = br.ReadInt32();
                br.ReadInt32(); // byte rate
                br.ReadInt16(); // block align
                bits = br.ReadInt16();
                br.BaseStream.Seek(size - 16, SeekOrigin.Current);
            }
            else if (id == "data")
            {
                data = br.ReadBytes(size);
            }
            else
            {
                br.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }

        if (data is null)
            throw new InvalidDataException($"{path}: no data chunk");
        if (channels != 1 || rate != 16000 || bits != 16)
            throw new InvalidDataException(
                $"{path}: need mono/16000Hz/16-bit PCM, got {channels}ch/{rate}Hz/{bits}-bit");

        var samples = new float[data.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
        return samples;
    }

    public static float[] ApplyGain(float[] samples, double gain)
    {
        if (Math.Abs(gain - 1.0) < 1e-9) return samples;
        var result = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
            result[i] = (float)Math.Clamp(samples[i] * gain, -1.0, 1.0);
        return result;
    }

    public static float[] PrependSilence(float[] samples, int ms, int sampleRate = 16000)
    {
        if (ms <= 0) return samples;
        var pad = ms * sampleRate / 1000;
        var result = new float[pad + samples.Length];
        Array.Copy(samples, 0, result, pad, samples.Length);
        return result;
    }

    public static float[] Prepare(float[] samples, double gain, int leadSilenceMs)
        => PrependSilence(ApplyGain(samples, gain), leadSilenceMs);

    /// <summary>MaxFrameRms uses 20 ms (320-sample) frames, matching
    /// InteriorSilenceSkipper's analysis frames — the quiet-talker guard is
    /// active while max frame RMS &lt; 0.002 / 0.15 ≈ 0.0133.</summary>
    public static (double Rms, double Peak, double MaxFrameRms) Stats(float[] samples, int frameSamples = 320)
    {
        double sum = 0, peak = 0, maxFrameRms = 0;
        for (var start = 0; start < samples.Length; start += frameSamples)
        {
            var end = Math.Min(start + frameSamples, samples.Length);
            double frameSum = 0;
            for (var i = start; i < end; i++)
            {
                var s = samples[i];
                frameSum += s * s;
                peak = Math.Max(peak, Math.Abs(s));
            }
            sum += frameSum;
            maxFrameRms = Math.Max(maxFrameRms, Math.Sqrt(frameSum / Math.Max(1, end - start)));
        }
        return (Math.Sqrt(sum / Math.Max(1, samples.Length)), peak, maxFrameRms);
    }
}
