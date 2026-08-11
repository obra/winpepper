#if WINDOWS
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.TranscribeCpp;
using Winpepper.Asr.TranscribeCpp.Worker;
using Winpepper.Asr.Transcription;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.IntegrationTests;

/// <summary>tbc0 delta-review round-1 fix: REAL contention measurement for the
/// listen-start prefetch launch. The stop-era evidence (2026-07-29 plan/evidence)
/// recorded in-process native calls reaching native_max=3960 ms when a recording-start
/// prefetch burst raced live streaming ASR; the July fix moved the burst to stop.
/// Post-fb1f538 the streaming ASR runs in the transcribe.cpp WORKER SUBPROCESS, and this
/// test measures — on the real Windows gate machine, with the real Nemotron worker, a
/// real streamed utterance, and REAL UIA/OCR bursts on the real foreground window — that
/// a listen-start burst no longer produces visible starvation on the app-side seams the
/// pipeline actually guards on:
///   * per-call duration of the app's own timed calls into the streaming path (the
///     NativeCallStats aggregates: the same numbers the timing line's native_max /
///     native_over250 render), and
///   * post-stop finish latency (stop → final transcript) with vs without the burst.
/// Worker-side native durations are not app-visible today (honest limit); the owner's
/// live-dictation readout covers native_max/native_over250 in production. Skips itself
/// plainly when the gate host has no foreground window or no Nemotron layout installed.
/// </summary>
[Trait("Platform", "Windows")]
public class WindowContextPrefetchAsrContentionTests
{
    private readonly ITestOutputHelper _log;
    public WindowContextPrefetchAsrContentionTests(ITestOutputHelper log) => _log = log;

    private static string ModelsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "winpepper", "models");

    private sealed class StubFallbackTranscriber : ITranscriber
    {
        public string ModelName => "stub-fallback";
        public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
            => Task.FromResult(new TranscriptionResult("", ModelName));
    }

    /// <summary>Real TranscribeWorkerHost exe discovery — same shape as
    /// Winpepper.Asr.Tests' WorkerHostProcessTests.HostPsi().</summary>
    private static ProcessStartInfo HostPsi()
    {
        var dir = AppContext.BaseDirectory;
        var apphost = Path.Combine(dir, "TranscribeWorkerHost.exe");
        if (File.Exists(apphost)) return new ProcessStartInfo(apphost);
        return new ProcessStartInfo(Environment.ProcessPath!,
            $"exec \"{Path.Combine(dir, "TranscribeWorkerHost.dll")}\"");
    }

    /// <summary>3.2 s of 220 Hz with a 5 Hz AM envelope at 16 kHz float mono —
    /// speech-like sustained energy; transcript content is irrelevant (latency only).</summary>
    private static float[] AmTone()
    {
        const int seconds = 3, hz = 16000;
        var samples = new float[seconds * hz + hz / 5]; // 3.2 s
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / hz;
            samples[i] = (float)(0.20 * Math.Sin(2 * Math.PI * 220 * t)
                                 * (1 + 0.25 * Math.Sin(2 * Math.PI * 5 * t)));
        }
        return samples;
    }

    /// <summary>One streaming dictation arm against the real worker. Feed at real-time
    /// pace in 200 ms chunks; when fireBursts, three REAL window-context prefetches
    /// (real UIA walk / WinRT OCR on the real foreground) launch at pushes 2/6/10.
    /// Returns per-arm measurements for the comparison.</summary>
    private sealed record ArmResult(
        int PostStopMs,
        int NativeMaxMs,
        int NativeOver250,
        int NativeCalls,
        double MaxPushElapsedMs,
        int PrefetchCompleted);

    private async Task<ArmResult> RunArmAsync(
        WorkerProcessEngine engine, float[] audio, IntPtr hwnd, bool fireBursts)
    {
        var transcriber = new NemotronStreamingTranscriber(
            () => engine, new StubFallbackTranscriber(), engine.ModelName, log: null);
        var prefetch = WindowContextPrefetch.CreateWindows(
            new UiaTreeReader(NullLogger<UiaTreeReader>.Instance),
            new OcrFallback(NullLogger<OcrFallback>.Instance),
            NullLogger<WindowContextPrefetch>.Instance);
        var coordinator = new WindowContextPrefetchCoordinator((h, ct) => prefetch.StartAsync(h, ct));
        coordinator.OnRecordingStart();

        var burstHandles = new List<WindowContextPrefetchHandle>();
        await using var session = await transcriber.StartSessionAsync(CancellationToken.None);

        const int chunk = 3200; // 200 ms at 16 kHz
        var maxPushMs = 0.0;
        var pushSw = new Stopwatch();
        for (int off = 0, push = 0; off < audio.Length; off += chunk, push++)
        {
            var take = Math.Min(chunk, audio.Length - off);
            if (fireBursts && (push == 2 || push == 6 || push == 10))
                burstHandles.Add(coordinator.Start(hwnd));
            pushSw.Restart();
            await session.PushAsync(audio.AsMemory(off, take), CancellationToken.None);
            pushSw.Stop();
            maxPushMs = Math.Max(maxPushMs, pushSw.Elapsed.TotalMilliseconds);
            await Task.Delay(200); // real-time pace
        }

        var finishSw = Stopwatch.StartNew();
        var result = await session.FinishAsync(audio, CancellationToken.None);
        finishSw.Stop();
        result.ShouldNotBeNull(); // result object always returned; text may be empty for a tone

        var stats = session is INativeCallStatsSource s ? s.NativeCallStats : null;
        var completed = 0;
        foreach (var h in burstHandles)
        {
            _ = await Task.WhenAny(h.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            if (h.Task.IsCompleted) completed++;
        }

        return new ArmResult(
            PostStopMs: (int)finishSw.ElapsedMilliseconds,
            NativeMaxMs: stats?.MaxMs ?? -1,
            NativeOver250: stats?.CountOver250Ms ?? -1,
            NativeCalls: stats?.Count ?? -1,
            MaxPushElapsedMs: maxPushMs,
            PrefetchCompleted: completed);
    }

    [Fact]
    public async Task ListenStartBurstOverlappingRealStreaming_NoVisibleStarvation_AndBoundedPostStop()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.SkipUnless(ForegroundWindow.Handle() != IntPtr.Zero,
            "no foreground window on this host — the real-burst contention evidence is not observable");
        var layout = StreamingModelLayout.English;
        Assert.SkipUnless(layout.IsInstalled(ModelsRoot),
            $"Nemotron layout not installed under {ModelsRoot} — cannot drive the real worker");

        var hwnd = ForegroundWindow.Handle();
        var audio = AmTone();
        using var engine = new WorkerProcessEngine(
            new ExeWorkerProcessFactory(HostPsi),
            layout.RuntimeDir(ModelsRoot), layout.GgufPath(ModelsRoot), layout.Name,
            log: msg => _log.WriteLine($"[worker] {msg}"));

        // Control arm: identical stream with NO burst.
        var control = await RunArmAsync(engine, audio, hwnd, fireBursts: false);
        // Burst arm: three real prefetch bursts overlap the live stream (the exact
        // exposure the listen-start launch creates).
        var burst = await RunArmAsync(engine, audio, hwnd, fireBursts: true);

        _log.WriteLine($"control: post_stop={control.PostStopMs}ms native_max={control.NativeMaxMs}ms native_over250={control.NativeOver250} calls={control.NativeCalls} max_push={control.MaxPushElapsedMs:0.0}ms");
        _log.WriteLine($"burst:   post_stop={burst.PostStopMs}ms native_max={burst.NativeMaxMs}ms native_over250={burst.NativeOver250} calls={burst.NativeCalls} max_push={burst.MaxPushElapsedMs:0.0}ms prefetch_completed={burst.PrefetchCompleted}/3");

        // The contention guard (same seam the timing line's native_max /
        // native_over250 render): no app-side timed call into the streaming path may
        // reach 250 ms — in EITHER arm.
        control.NativeOver250.ShouldBe(0);
        burst.NativeOver250.ShouldBe(0);
        burst.NativeMaxMs.ShouldBeLessThan(250,
            "\nA native path call exceeded 250 ms with the listen-start burst overlapping live streaming.");

        // The burst work actually happened (else the comparison is empty).
        burst.PrefetchCompleted.ShouldBe(3);

        // Total post-stop latency: burst arm bounded absolutely AND relative to the
        // control (a contention regression larger than what ctx_wait saves would show here).
        control.PostStopMs.ShouldBeLessThan(3000, "\ncontrol-arm finish exceeded 3 s on 3.2 s of audio");
        burst.PostStopMs.ShouldBeLessThan(Math.Max(control.PostStopMs * 2, control.PostStopMs + 500),
            $"\nburst-arm post-stop ({burst.PostStopMs}ms) exceeded twice the control ({control.PostStopMs}ms)");
        burst.PostStopMs.ShouldBeLessThan(5000);
    }
}
#endif
