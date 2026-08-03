# Start-Cue Silence-Gate Mask Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Stop WinPepper's own start-cue beep from contaminating the
silence-gate decision by excluding a runtime-computed head window from
`SilenceTrimmer`'s decision math — with the cue length measured from the
shipped `start.wav` at startup, never hardcoded.

**Architecture:** Three pure, Linux-tested pieces in `Winpepper.Audio`
(a header-only WAV duration probe `WavDuration`; mask-window arithmetic
`StartCueGateMask`, which becomes the single source of the 500 ms warm
pre-roll REQUEST; a new optional `maskMs` parameter on
`SilenceTrimmer.Trim` that excludes head frames from the gate DECISION
statistics only — trim thresholds, offsets, and the output buffer are
computed over all frames exactly as before). Thin Windows-side wiring:
`WinUiSoundEffectPlayer` measures its own asset once at construction and
exposes it via `ISoundEffectPlayer.StartCueMs`;
`IWarmAudioRecorder.StartSession` returns the pre-roll milliseconds it
ACTUALLY seeded (so the mask shrinks when prewarm is off or the ring is
short); `PipelineHost` computes the preroll-aware mask per-dictation
(gated on the player's actual `Enabled` state) and adds `cue mask`
observability to the silent-drop log line plus one startup INF line.

**Tech Stack:** C# / .NET 9 (dual-TFM `Winpepper.Audio`, Windows-only
`Winpepper.App`), xUnit v3 1.0.0 + Shouldly 4.2.1 (run via
`dotnet exec`, never `dotnet test`), Serilog behind
Microsoft.Extensions.Logging.

## Global Constraints

- Repo root (worktree, branch `feat/start-cue-gate-mask`, base `main @ 7345ca1`):
  `/home/dan/code/winpepper/.worktrees/start-cue-gate-mask` — run ALL commands from here.
  Do NOT touch the main checkout at `/home/dan/code/winpepper` (it has unrelated
  uncommitted changes from a parallel session).
- Linux suite green before EVERY commit: `./scripts/linux-tests.sh` — **NEVER `dotnet test`**.
  (The script's `DOTNET_ROOT` defaults to `/home/dan/code/winpepper/.dotnet` — the shared
  gitignored SDK; that is expected and correct when running from this worktree.)
- Full Windows gate before done: `./scripts/windows-gate.sh` (expect `GATE: GREEN`).
  UNC MSB4025 + vsock interop flakes are known transients — retry up to 3 times.
- Never mix Linux- and Windows-side builds in the same `bin/`/`obj/` (the scripts clean for you).
- Do NOT push. Leave the branch local — the root session merges, gates, and installs.
- **Owner requirement — never hardcode the cue duration**: cue-duration literals
  (`150`, `240`, `320`) and combined-window literals (`900`, `1000`) remain banned in
  mask math. The only literals allowed are the pre-roll (`500`, single-sourced via
  `StartCueGateMask.WarmPrerollMs`) and the two named margin constants
  (`CueStartLatencyMarginMs = 200`, `CueDecayMarginMs = 150`). Note: `150` as the
  DECAY MARGIN value is a margin constant, not a hardcoded cue length — the cue
  length is always the runtime-measured value. The combined window must remain a
  computed sum, never a literal.
- Gate constants UNCHANGED: `SilentSpeechLevel 0.004`, `MinVoicedDurationMs 600`,
  `ClearSpeechRmsFloor 0.02`, `MinClearVoicedDurationMs 100`. Trimming constants unchanged.
- `maskMs = 0` must be byte-identical to today's behavior; the mask must NEVER change
  trimming offsets, `RemovedMs`/`RunsTrimmed`, or the `Trimmed` buffer content.
- Out of scope: spectral/periodicity voicedness (recorded as future fix for the
  rustle/thump residuals), streaming feed, trimming behavior, cue playback itself,
  history retention, any new settings/UI knobs.
- Commit style: Conventional Commits with project scopes (`feat(audio):`, `feat(core):`,
  `feat(app):`, `docs(plans):`), long declarative subjects, and this exact trailer
  (blank line between the two lines) on every commit:

  ```
  🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

  Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
  ```

---

## Background (read once — everything below builds on it)

The measured problem (investigation 2026-08-02, artifacts `/tmp/gate-inv/`):

- `PipelineHost` fires `_sounds.PlayStart()` at the hotkey (`PipelineHost.cs:500`
  hold / `:1082` toggle), then starts recording with a retroactive ~500 ms warm
  pre-roll (`_warmRecorder!.StartSession(includePrerollMs: 500)` at `:543` / `:1119`).
  So **buffer t=0 is ~500 ms BEFORE the hotkey**, and the 150 ms cue
  (`src/Winpepper.App/Assets/start.wav`, 22050 Hz mono 16-bit, 440→660 Hz) lands at
  ~592–861 ms into every fully-seeded warm buffer at frame RMS up to 0.05 — above
  the gate's 0.02 clear-speech tier. With `MinClearVoicedDurationMs = 100` and a
  pickup of up to ~230 ms (150 ms emission + a measured tail of at most 81 ms),
  the cue alone can satisfy the clear-speech escape hatch
  (confirmed escape 2026-08-02 20:18:00: voiced=360 ms, clear=120 ms — the 120 ms
  was entirely beep), and every silent drop logs clear=60–160 ms, destroying the
  recalibration margin.
- The plan's EXACT dual-threshold mask semantics (decision stats post-mask, trim
  math over all frames — Task 3) were re-implemented and run over the frozen
  100-recording archive (validation 2026-08-02/03, 98 usable recordings): at the
  1000 ms warm window, **0 of 91** real gate-passing dictations flip pass→drop and
  **0 of 6** true-silent drops flip drop→pass; the confirmed beep-only escape
  correctly flips to drop; the tightest passer keeps a 140 ms margin. This
  exact-semantics result supersedes the investigation's earlier counting-only
  "~1000 ms ⇒ 0 of 91" measurement. Evidence:
  `/home/dan/code/winpepper/.worktrees/.the-usual-logs/start-cue-gate-mask/`
  (`load-bearing-ledger.md` + `reports/`).

Two structural facts that dictate the design (found by code inspection):

1. **The P90/P10 percentiles are not decision-only.** In today's
   `SilenceTrimmer.Trim`, `threshold = min(max(3·P10, 0.002), 0.15·P90)` produces
   `isSilence[]`, which is the *sole* input to the trim-run walker. Masking frames
   out of the percentile sample would therefore move trimming offsets. So the
   implementation computes **two thresholds**: a *decision* threshold from post-mask
   frames (drives `IsSilent`, `VoicedMs`, `ClearVoicedMs`, `MaxFrameRms`) and a
   *trim* threshold from ALL frames, bit-identical to today (drives `isSilence[]`
   and the walker). With `maskMs = 0` the two are the same computation.
2. **`AppSettings.PlaySounds` exists** (default `true`, `AppSettings.cs:74`) and is
   applied ONCE at boot to `WinUiSoundEffectPlayer.Enabled` (`AppShell.cs:194`);
   nothing re-applies it when the settings toggle flips, and `PlayStart()` gates on
   `Enabled` (`WinUiSoundEffectPlayer.cs:21`). So the mask must be gated on
   **`_sounds.Enabled`** (what the player actually did), not on a settings snapshot.

Mask window: `[0, actualPrerollMs + CueStartLatencyMarginMs + measuredCueMs + CueDecayMarginMs]`,
where `actualPrerollMs` is the pre-roll the recorder ACTUALLY seeded this session
(`IWarmAudioRecorder.StartSession`'s return value, ≤ the requested `WarmPrerollMs`).
Margin justification (validation 2026-08-02/03 over the frozen 100-recording
archive, 98 usable; recorded here, referenced by the constants' XML docs):

- `CueStartLatencyMarginMs = 200`: `SoundPlayer.Play()` is async fire-and-forget;
  measured cue start latency after the hotkey (two independent detectors, 98
  recordings) is min 92 / p50 115 / p90 131 / max 144 ms — the investigation's
  earlier single "~20 ms dispatch latency" observation was FALSIFIED (0/98
  recordings started within 50 ms); cpu-pegged starts (N=3) sat at 130–144 ms.
  Measured onset is therefore 592–644 ms into a fully-seeded warm buffer
  (= 500 pre-roll + 92–144 ms latency). Margin 200 leaves 56 ms of headroom over
  the observed maximum.
- `CueDecayMarginMs = 150`: room decay + WASAPI capture smearing extend the mic
  pickup at most 81 ms beyond the 150 ms emission (22 overlap-free recordings);
  pickup ends by 861 ms into the buffer at the latest. Margin 150 leaves 69 ms of
  headroom over the observed maximum. (The VALUE 150 coincides with the current
  asset's length by accident — it is a margin constant, never a cue length.)
- Warm window with the shipped asset: 500 + 200 + 150 (cue) + 150 = 1000 ms —
  always a computed sum, never a literal. Safety re-validated with the plan's
  EXACT dual-threshold semantics over the archive: 0/91 real pass→drop and 0/6
  drop→pass at 1000 ms; the beep-only escape correctly flips to drop; tightest
  passer margin 140 ms (see the Background bullet above for the evidence dir).
- The pre-roll is best-effort (`min(500 ms, ring contents)`, zero when prewarm is
  off) — which is exactly why the mask uses the ACTUAL per-session pre-roll, not
  the worst-case constant: a cold-mode simulation showed a fixed worst-case mask
  flips 4/91 real dictations at 1000 ms while the preroll-aware mask flips 0/91 —
  and the fixed-window cold cost grows with every margin correction (1→4 flips
  going 900→1000). `StartSession` returns the actually-seeded pre-roll ms so the
  window shrinks with it (~500 ms in cold mode).

Accepted residual (documented + pinned by test): an utterance spoken ENTIRELY inside
the mask window (i.e. the user spoke only before/at the hotkey and stopped within
~1000 ms of warm buffer start — the window shrinks with the actual pre-roll, to
~500 ms cold) is now classified silent. Real speech that *starts* inside the window
still passes when enough voiced audio remains after it.

---

## File Structure

| File | Action | Responsibility |
|---|---|---|
| `src/Winpepper.Audio/WavDuration.cs` | Create | Header-only, non-throwing WAV duration probe (`TryMeasureMs`). Fifth RIFF reader in the repo by design: unlike the four existing throwing readers, this one must fail open on truncated/corrupt/non-PCM input. |
| `src/Winpepper.Audio/StartCueGateMask.cs` | Create | Mask-window arithmetic + `WarmPrerollMs = 500` (single source of the pre-roll REQUEST). |
| `src/Winpepper.Audio/SilenceTrimmer.cs` | Modify | `Trim(samples, int maskMs = 0)`: decision stats over post-mask frames; trim math over all frames, unchanged. |
| `src/Winpepper.Audio/IWarmAudioRecorder.cs` | Modify | `StartSession` returns `int` — the pre-roll ms ACTUALLY seeded (was `void`). |
| `src/Winpepper.Audio/WarmWasapiRecorder.cs` | Modify | (`#if WINDOWS`) `StartSession` returns `preroll.Length / (SampleRate16k / 1000)` from the seeded ring samples. |
| `src/Winpepper.Core/Audio/ISoundEffectPlayer.cs` | Modify | Add `int StartCueMs { get; }`. |
| `src/Winpepper.Core/Audio/NoopSoundEffectPlayer.cs` | Modify | `StartCueMs => 0`. |
| `src/Winpepper.App/Audio/WinUiSoundEffectPlayer.cs` | Modify | Measure `start.wav` duration once in ctor via `WavDuration`. |
| `src/Winpepper.App/Hosting/PipelineHost.cs` | Modify | Use `StartCueGateMask.WarmPrerollMs` at both `StartSession` sites and capture the returned actual pre-roll into `_lastSessionPrerollMs` in BOTH arms; compute the preroll-aware mask per-dictation; startup INF/WRN; `cue mask` on the silent-drop line. |
| `src/Winpepper.Asr/InteriorSilenceSkipper.cs` | Modify (comments only) | Refresh the three `SilenceTrimmer.cs:NN` line-range citations that shift with the edit. |
| `src/Winpepper.Asr/Transcription/ParakeetStreamingSession.cs` | Modify (comments only) | Note the new batch-vs-streaming divergence (streaming has no cue mask). |
| `tests/Winpepper.Audio.Tests/WavDurationTests.cs` | Create | Parser tests: valid/truncated/corrupt/zero-length/non-PCM/odd-chunk/missing-file. |
| `tests/Winpepper.Audio.Tests/StartCueGateMaskTests.cs` | Create | Mask arithmetic tests incl. disabled ⇒ 0, unmeasured cue ⇒ 0, and preroll-aware sizing (warm 500 ⇒ 1000, cold 0 ⇒ 500, partial 300 ⇒ 800, negative preroll clamps). |
| `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs` | Modify (append only) | 9 new mask tests. The existing 23 tests are NOT touched — they are the mask=0 characterization suite. |
| `tests/Winpepper.Core.Tests/Audio/NoopSoundEffectPlayerTests.cs` | Modify | Pin `StartCueMs == 0`. |
| `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md` | Modify (append section) | 08-02 escape-investigation summary + gates record. |

Everything testable is `net9.0`-pure (`Winpepper.Audio`, `Winpepper.Core`) and runs on
the Linux gate. `Winpepper.App` changes are compile-verified by the Windows gate
(no test project references `Winpepper.App` — established house pattern). The
`IWarmAudioRecorder` interface change compiles in `Winpepper.Audio`'s Linux-built
`net9.0` TFM, but its sole implementor `WarmWasapiRecorder` is `#if WINDOWS` (no
Linux-compiled implementor or test fake exists), so the recorder change is
compile-verified by the Windows gate alongside `PipelineHost`.

---

### Task 1: `WavDuration` — non-throwing header-only WAV duration probe

**Files:**
- Create: `src/Winpepper.Audio/WavDuration.cs`
- Test: `tests/Winpepper.Audio.Tests/WavDurationTests.cs`

**Interfaces:**
- Consumes: nothing (BCL only — do NOT use NAudio; repo policy is hand-rolled RIFF
  to stay Linux-buildable, see `src/Winpepper.History/WavWriter.cs:5-6`).
- Produces: `public static bool WavDuration.TryMeasureMs(string path, out int durationMs)`
  in namespace `Winpepper.Audio`. Returns `true` with the exact
  `sampleFrames * 1000 / sampleRate` duration for a well-formed PCM WAV
  (including `true`/`0` for a zero-length data chunk); returns `false` with
  `durationMs = 0` for missing file, zero-length file, non-RIFF garbage, truncated
  header or data chunk, non-PCM format tag, or zero sample-rate/block-align.
  Task 4 calls this from `WinUiSoundEffectPlayer`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Audio.Tests/WavDurationTests.cs` with exactly:

```csharp
using Shouldly;
using Winpepper.Audio;
using Xunit;

namespace Winpepper.Audio.Tests;

/// <summary>
/// WavDuration is a HEADER-ONLY, NON-THROWING duration probe for the start-cue
/// asset. Unlike the repo's four existing RIFF readers (WavWriter, PcmWavEncoder's
/// mirror, BenchAudio, the Asr test helpers), it must FAIL OPEN — return false —
/// on anything malformed, because a failed measurement merely disables the
/// silence-gate cue mask (mask 0 = today's behavior). Bytes are synthesized
/// in-test (temp dir + Guid + IDisposable, mirroring WavWriterTests) because
/// checked-in corrupt fixtures would be opaque in review.
/// </summary>
public sealed class WavDurationTests : IDisposable
{
    private readonly string _dir;

    public WavDurationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"wavduration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Write(string name, byte[] bytes)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    /// <summary>
    /// Canonical RIFF/WAVE bytes: RIFF + WAVE + optional odd-sized JUNK chunk +
    /// 16-byte fmt + data (zero-filled). Defaults mirror the real shipped
    /// start.wav header: 22050 Hz mono 16-bit PCM, 6616 data bytes = 150 ms.
    /// </summary>
    private static byte[] WavBytes(
        short formatTag = 1,
        short channels = 1,
        int sampleRate = 22050,
        short bitsPerSample = 16,
        int dataBytes = 6616,
        short? blockAlignOverride = null,
        bool junkChunkBeforeFmt = false)
    {
        var blockAlign = blockAlignOverride ?? (short)(channels * bitsPerSample / 8);
        var byteRate = sampleRate * blockAlign;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes); // riff size — parser must not trust it
        w.Write("WAVE"u8);
        if (junkChunkBeforeFmt)
        {
            w.Write("JUNK"u8);
            w.Write(3);              // odd size — walker must pad to 4
            w.Write(new byte[4]);    // 3 payload + 1 pad byte
        }
        w.Write("fmt "u8);
        w.Write(16);
        w.Write(formatTag);
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write(bitsPerSample);
        w.Write("data"u8);
        w.Write(dataBytes);
        w.Write(new byte[dataBytes]);
        w.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void TryMeasureMs_RealStartCueShapedHeader_Returns150()
    {
        // Mirrors the shipped asset exactly: 6616 data bytes / 2 blockAlign
        // = 3308 frames; 3308 * 1000 / 22050 = 150 ms (integer division).
        var path = Write("start-shaped.wav", WavBytes());

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeTrue();
        ms.ShouldBe(150);
    }

    [Fact]
    public void TryMeasureMs_16kMonoOneSecond_Returns1000()
    {
        // 32000 bytes / 2 = 16000 frames at 16000 Hz = exactly 1000 ms —
        // same shape as tests/Winpepper.Asr.Tests/fixtures/tone-440hz-1s.wav.
        var path = Write("one-second.wav", WavBytes(sampleRate: 16000, dataBytes: 32000));

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeTrue();
        ms.ShouldBe(1000);
    }

    [Fact]
    public void TryMeasureMs_UnknownOddSizedChunkBeforeFmt_IsSkippedWithPadding()
    {
        var path = Write("junk-chunk.wav", WavBytes(junkChunkBeforeFmt: true));

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeTrue();
        ms.ShouldBe(150);
    }

    [Fact]
    public void TryMeasureMs_ZeroLengthDataChunk_ReturnsTrueZeroMs()
    {
        // A structurally valid but empty cue: parse SUCCEEDS with 0 ms.
        // The caller (StartCueGateMask.ComputeMaskMs) maps duration <= 0 to
        // mask 0, so an inaudible cue never masks anything.
        var path = Write("empty-data.wav", WavBytes(dataBytes: 0));

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeTrue();
        ms.ShouldBe(0);
    }

    [Fact]
    public void TryMeasureMs_MissingFile_ReturnsFalse()
    {
        var path = Path.Combine(_dir, "does-not-exist.wav");

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeFalse();
        ms.ShouldBe(0);
    }

    [Fact]
    public void TryMeasureMs_ZeroLengthFile_ReturnsFalse()
    {
        var path = Write("zero-bytes.wav", Array.Empty<byte>());

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeFalse();
        ms.ShouldBe(0);
    }

    [Fact]
    public void TryMeasureMs_GarbageBytes_ReturnsFalse()
    {
        var garbage = new byte[64];
        Array.Fill(garbage, (byte)0xAB);
        var path = Write("garbage.wav", garbage);

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeFalse();
        ms.ShouldBe(0);
    }

    [Fact]
    public void TryMeasureMs_TruncatedDataChunk_ReturnsFalse()
    {
        // Header claims 6616 data bytes but the file is cut 100 bytes into
        // the data chunk. A duration computed from the CLAIMED size would be
        // a lie — fail open instead.
        var whole = WavBytes();
        var truncated = whole[..(44 + 100)];
        var path = Write("truncated-data.wav", truncated);

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeFalse();
        ms.ShouldBe(0);
    }

    [Fact]
    public void TryMeasureMs_TruncatedMidHeader_ReturnsFalse()
    {
        var path = Write("truncated-header.wav", WavBytes()[..20]);

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeFalse();
        ms.ShouldBe(0);
    }

    [Theory]
    [InlineData((short)3)]                    // IEEE float
    [InlineData(unchecked((short)0xFFFE))]    // WAVE_FORMAT_EXTENSIBLE
    public void TryMeasureMs_NonPcmFormatTag_ReturnsFalse(short formatTag)
    {
        var path = Write($"non-pcm-{(ushort)formatTag}.wav", WavBytes(formatTag: formatTag));

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeFalse();
        ms.ShouldBe(0);
    }

    [Fact]
    public void TryMeasureMs_ZeroBlockAlign_ReturnsFalse()
    {
        // Guards the frames = dataBytes / blockAlign division.
        var path = Write("zero-blockalign.wav", WavBytes(blockAlignOverride: 0));

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeFalse();
        ms.ShouldBe(0);
    }
}
```

- [ ] **Step 2: Run the suite to verify the new tests fail**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask && ./scripts/linux-tests.sh
```
Expected: FAILURE — the `Winpepper.Audio.Tests` project does not build
(`CS0103`/`CS0246`: `WavDuration` does not exist). Script exits non-zero.

- [ ] **Step 3: Write the implementation**

Create `src/Winpepper.Audio/WavDuration.cs` with exactly:

```csharp
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
```

- [ ] **Step 4: Run the suite to verify it passes**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask && ./scripts/linux-tests.sh
```
Expected: PASS — exit 0, every project reports `Failed: 0` / `Errors: 0`
(12 new tests in `Winpepper.Audio.Tests`).

- [ ] **Step 5: Sanity-check against the real shipped asset**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask && python3 - <<'EOF'
import struct
with open('src/Winpepper.App/Assets/start.wav','rb') as f: b=f.read()
# fmt at offset 12 in this canonical file: rate at 24, blockAlign at 32, data size at 40
rate=struct.unpack_from('<I',b,24)[0]; ba=struct.unpack_from('<H',b,32)[0]; ds=struct.unpack_from('<I',b,40)[0]
print(rate, ba, ds, ds//ba*1000//rate)
EOF
```
Expected output: `22050 2 6616 150` — confirming the test's default `WavBytes()`
mirrors the real asset and the production file measures 150 ms.

- [ ] **Step 6: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask
git add src/Winpepper.Audio/WavDuration.cs tests/Winpepper.Audio.Tests/WavDurationTests.cs
git commit -m "$(cat <<'EOF'
feat(audio): add WavDuration — non-throwing header-only WAV probe so the start-cue length is measured, never hardcoded

Fail-open by contract: missing/corrupt/truncated/non-PCM input returns
false ⇒ the silence-gate cue mask stays 0 and the gate behaves as today.
Fifth hand-rolled RIFF reader by design — the four existing ones throw
and decode samples; this one must do neither.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 2: `StartCueGateMask` — mask arithmetic + single source of the 500 ms pre-roll request

**Files:**
- Create: `src/Winpepper.Audio/StartCueGateMask.cs`
- Test: `tests/Winpepper.Audio.Tests/StartCueGateMaskTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (Task 5 consumes all of these from `PipelineHost`):
  - `public const int StartCueGateMask.WarmPrerollMs = 500` — the pre-roll REQUEST
    `PipelineHost` passes to `StartSession` (the mask itself uses the ACTUAL
    seeded pre-roll `StartSession` returns).
  - `public const int StartCueGateMask.CueStartLatencyMarginMs = 200`
  - `public const int StartCueGateMask.CueDecayMarginMs = 150`
  - `public static int StartCueGateMask.ComputeMaskMs(int actualPrerollMs, int cueMs, bool soundsEnabled)`
    — returns `0` when `soundsEnabled` is false or `cueMs <= 0`,
    else `Math.Max(actualPrerollMs, 0) + CueStartLatencyMarginMs + cueMs + CueDecayMarginMs`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Audio.Tests/StartCueGateMaskTests.cs` with exactly:

```csharp
using Shouldly;
using Winpepper.Audio;
using Xunit;

namespace Winpepper.Audio.Tests;

public class StartCueGateMaskTests
{
    [Fact]
    public void ComputeMaskMs_WarmFullPreroll_AddsPrerollAndBothMargins()
    {
        // With the shipped 150 ms cue and a fully-seeded warm pre-roll:
        // 500 + 200 + 150 + 150 = 1000 ms. Re-validated 2026-08-02/03 with
        // the plan's exact dual-threshold semantics over the frozen
        // 100-recording archive: 0/91 real pass->drop, 0/6 drop->pass at
        // this window; tightest passer margin 140 ms. The 500/150 here are
        // TEST inputs, not production constants — production feeds the
        // recorder's actually-seeded pre-roll and the runtime-measured WAV.
        StartCueGateMask.ComputeMaskMs(500, 150, soundsEnabled: true).ShouldBe(1000);
        StartCueGateMask.ComputeMaskMs(500, 150, soundsEnabled: true).ShouldBe(
            StartCueGateMask.WarmPrerollMs
            + StartCueGateMask.CueStartLatencyMarginMs
            + 150
            + StartCueGateMask.CueDecayMarginMs);
    }

    [Fact]
    public void ComputeMaskMs_ColdZeroPreroll_ShrinksToMarginsPlusCue()
    {
        // Prewarm off (or fully drained ring): buffer t=0 IS the hotkey, so
        // only latency + cue + decay need masking: 0 + 200 + 150 + 150 = 500.
        // Cold-mode simulation over the archive: this preroll-aware mask
        // flips 0/91 real dictations where a fixed worst-case 1000 ms mask
        // flips 4/91 — the reason the pre-roll is plumbed per-session.
        StartCueGateMask.ComputeMaskMs(0, 150, soundsEnabled: true).ShouldBe(500);
    }

    [Fact]
    public void ComputeMaskMs_PartialPreroll_ShrinksWithTheActualSeed()
    {
        // Partially drained ring: only 300 ms actually seeded ->
        // 300 + 200 + 150 + 150 = 800. The window follows the recorder's
        // honest report, never the request.
        StartCueGateMask.ComputeMaskMs(300, 150, soundsEnabled: true).ShouldBe(800);
    }

    [Fact]
    public void ComputeMaskMs_NegativePreroll_ClampsToZeroPreroll()
    {
        StartCueGateMask.ComputeMaskMs(-50, 150, soundsEnabled: true).ShouldBe(
            StartCueGateMask.ComputeMaskMs(0, 150, soundsEnabled: true));
    }

    [Fact]
    public void ComputeMaskMs_SoundsDisabled_ReturnsZero()
    {
        // PlaySounds off ⇒ the player never emits the cue ⇒ nothing to mask.
        StartCueGateMask.ComputeMaskMs(500, 150, soundsEnabled: false).ShouldBe(0);
    }

    [Fact]
    public void ComputeMaskMs_UnmeasuredCue_ReturnsZero()
    {
        // WavDuration failed (missing/corrupt start.wav) ⇒ FAIL OPEN: the
        // gate behaves exactly as it did before the mask existed.
        StartCueGateMask.ComputeMaskMs(500, 0, soundsEnabled: true).ShouldBe(0);
    }

    [Fact]
    public void ComputeMaskMs_NegativeCueDuration_ReturnsZero()
    {
        StartCueGateMask.ComputeMaskMs(500, -5, soundsEnabled: true).ShouldBe(0);
    }

    [Fact]
    public void WarmPrerollMs_IsThePipelinesPrerollRequest()
    {
        // Pin the single-source contract: PipelineHost passes THIS constant to
        // StartSession(includePrerollMs:) at both hotkey arms and feeds the
        // RETURNED actual pre-roll back into ComputeMaskMs. If this value
        // changes, the request follows automatically — that is the point.
        StartCueGateMask.WarmPrerollMs.ShouldBe(500);
    }
}
```

- [ ] **Step 2: Run the suite to verify the new tests fail**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask && ./scripts/linux-tests.sh
```
Expected: FAILURE — `Winpepper.Audio.Tests` does not build
(`CS0103`/`CS0246`: `StartCueGateMask` does not exist).

- [ ] **Step 3: Write the implementation**

Create `src/Winpepper.Audio/StartCueGateMask.cs` with exactly:

```csharp
namespace Winpepper.Audio;

/// <summary>
/// Computes the head-of-buffer window that <see cref="SilenceTrimmer"/>
/// excludes from its silence-gate DECISION because the app's own start cue
/// contaminates it (validated 2026-08-02/03 over the frozen 100-recording
/// archive: the 150 ms cue is picked up by the mic at ~592-861 ms into every
/// fully-seeded warm buffer at frame RMS up to 0.05 — above the 0.02
/// clear-speech tier — because recording starts with a retroactive warm
/// pre-roll, so buffer t=0 is ~<see cref="WarmPrerollMs"/> before the hotkey
/// and the cue becomes audible 92-144 ms after it).
///
/// Window = actual seeded pre-roll + CueStartLatencyMarginMs + measured cue
/// duration + CueDecayMarginMs. The cue duration is measured at runtime from
/// the shipped WAV (<see cref="WavDuration"/>) — NEVER hardcoded (owner
/// requirement: the asset may change or become user-configurable). The
/// pre-roll is what the recorder ACTUALLY seeded this session
/// (IWarmAudioRecorder.StartSession's return), not the requested worst case:
/// a shorter/zero pre-roll (prewarm off, drained ring) moves the cue EARLIER
/// and the window shrinks with it. Cold-mode validation: a fixed worst-case
/// window flips 4/91 real dictations at 1000 ms; the preroll-aware window
/// flips 0/91.
///
/// Sizing evidence (exact plan semantics over the archive): with the current
/// 150 ms asset the warm window is 1000 ms; 0/91 real gate-passing
/// dictations flip pass->drop, 0/6 true-silent drops flip drop->pass, the
/// confirmed beep-only escape correctly flips to drop, and the tightest
/// passer keeps a 140 ms margin.
/// </summary>
public static class StartCueGateMask
{
    /// <summary>
    /// Warm pre-roll the pipeline REQUESTS at session start. THE single
    /// source of this number: PipelineHost passes it to
    /// StartSession(includePrerollMs:) at both hotkey arms — do not
    /// duplicate the literal. The mask itself is built from the ACTUAL
    /// seeded pre-roll StartSession returns (&lt;= this request).
    /// </summary>
    public const int WarmPrerollMs = 500;

    /// <summary>
    /// Dispatch + render latency between PlayStart() returning and the cue
    /// being audible (SoundPlayer.Play is async fire-and-forget). Measured
    /// over the frozen archive (98 recordings, two independent detectors):
    /// min 92 / p50 115 / p90 131 / max 144 ms; cpu-pegged starts (N=3) at
    /// 130-144 ms. 200 ms leaves 56 ms of headroom over the observed max
    /// (an earlier single ~20 ms observation was falsified — 0/98 within
    /// 50 ms).
    /// </summary>
    public const int CueStartLatencyMarginMs = 200;

    /// <summary>
    /// Room decay/reverb + capture smearing after the cue's emission ends:
    /// measured pickup tail beyond the 150 ms emission is at most 81 ms
    /// (22 overlap-free recordings); pickup ends by 861 ms into the warm
    /// buffer. 150 ms leaves 69 ms of headroom over the observed max. NOTE:
    /// this margin VALUE coincides with the current asset's 150 ms length —
    /// it is a margin constant, not a hardcoded cue duration (the cue
    /// length is always the measured cueMs argument).
    /// </summary>
    public const int CueDecayMarginMs = 150;

    /// <summary>
    /// The mask duration SilenceTrimmer should exclude from its decision.
    /// <paramref name="actualPrerollMs"/> is the pre-roll the recorder
    /// ACTUALLY seeded this session (StartSession's return; 0 in cold mode,
    /// negative clamps to 0), so the window shrinks when less pre-hotkey
    /// audio exists. 0 when the cue is disabled (nothing played ⇒ nothing
    /// to mask) or its duration could not be measured (FAIL OPEN: gate
    /// behaves as before the mask existed).
    /// </summary>
    public static int ComputeMaskMs(int actualPrerollMs, int cueMs, bool soundsEnabled)
    {
        if (!soundsEnabled || cueMs <= 0) return 0;
        return Math.Max(actualPrerollMs, 0) + CueStartLatencyMarginMs + cueMs + CueDecayMarginMs;
    }
}
```

- [ ] **Step 4: Run the suite to verify it passes**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask && ./scripts/linux-tests.sh
```
Expected: PASS — exit 0, all projects `Failed: 0` / `Errors: 0`.

- [ ] **Step 5: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask
git add src/Winpepper.Audio/StartCueGateMask.cs tests/Winpepper.Audio.Tests/StartCueGateMaskTests.cs
git commit -m "$(cat <<'EOF'
feat(audio): add StartCueGateMask — cue-mask window arithmetic and single source of the 500 ms warm pre-roll request

mask = actual seeded preroll + 200 ms start-latency + runtime-measured
cue + 150 ms decay; 0 when the cue is disabled or unmeasured (fail
open). Margins measured over the frozen 100-recording archive
(2026-08-02/03: cue start latency 92-144 ms, pickup end <= 861 ms);
preroll-aware sizing flips 0/91 real dictations in the cold simulation
vs 4/91 for a fixed worst-case window.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 3: `SilenceTrimmer` cue mask — decision math only, trimming untouched

**Files:**
- Modify: `src/Winpepper.Audio/SilenceTrimmer.cs` (the `Trim` method, `:118-281`, plus
  three `TrimResult` XML doc lines)
- Modify: `src/Winpepper.Asr/InteriorSilenceSkipper.cs` (comments at `:154`, `:184`, `:186` only)
- Modify: `src/Winpepper.Asr/Transcription/ParakeetStreamingSession.cs` (comment at `:61-73` only)
- Test: `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs` (append 9 tests; do NOT
  touch the existing 23 — they are the mask=0 characterization suite and must stay
  green unchanged)

**Interfaces:**
- Consumes: existing private helpers `Percentile`, `AppendKeep`, `AudioEnergy.Rms`,
  and the test file's existing synth helpers `Dc(double rms, int ms)` /
  `Join(params float[][])` (defined at `SilenceTrimmerTests.cs:13-33`; `Dc` builds a
  DC block whose every 20 ms frame has RMS exactly equal to the amplitude).
- Produces: `public static TrimResult SilenceTrimmer.Trim(ReadOnlySpan<float> samples, int maskMs = 0)`
  — optional parameter, so all 23 existing test call sites and the bench
  (`scripts/asr-latency-bench/Program.cs:464`) keep compiling unchanged. Task 5 passes
  a real mask from `PipelineHost`. Semantics: frames in `[0, ceil(maskMs/20ms))` are
  excluded from the P90-silent gate, the decision threshold's percentiles, and the
  `VoicedMs`/`ClearVoicedMs`/`MaxFrameRms` counting; the trim threshold, `isSilence[]`,
  the walker, `RemovedMs`, `RunsTrimmed`, and `Trimmed` are computed over ALL frames
  exactly as before. Negative mask ⇒ 0. Mask covering every frame ⇒ `IsSilent = true`
  with zeroed observability fields (and no crash).

- [ ] **Step 1: Write the failing tests**

Append to the end of the test class in
`tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs` (before the class's closing
brace), exactly:

```csharp
    // ------------------------------------------------------------------
    // Start-cue mask (2026-08-02): maskMs excludes the head window from the
    // gate DECISION only. 1000 ms below = 500 preroll + 200 latency +
    // 150 cue + 150 decay for the shipped asset on a fully-seeded warm
    // session — a representative value, computed in production by
    // StartCueGateMask from the actual seeded pre-roll and the
    // runtime-measured cue.
    // ------------------------------------------------------------------

    [Fact]
    public void Trim_MaskZero_IsIdenticalToUnmasked()
    {
        // Characterization pin: maskMs = 0 must be byte-identical to the
        // one-argument call on a kept, clear-tier recording.
        var buf = Join(Dc(0.001, 840), Dc(0.05, 300), Dc(0.001, 860));

        var r0 = SilenceTrimmer.Trim(buf);
        var rm = SilenceTrimmer.Trim(buf, 0);

        rm.IsSilent.ShouldBe(r0.IsSilent);
        rm.VoicedMs.ShouldBe(r0.VoicedMs);
        rm.ClearVoicedMs.ShouldBe(r0.ClearVoicedMs);
        rm.MaxFrameRms.ShouldBe(r0.MaxFrameRms);
        rm.RemovedMs.ShouldBe(r0.RemovedMs);
        rm.RunsTrimmed.ShouldBe(r0.RunsTrimmed);
        rm.Trimmed.SequenceEqual(r0.Trimmed).ShouldBeTrue();
    }

    [Fact]
    public void Trim_NegativeMask_TreatedAsZero()
    {
        var buf = Join(Dc(0.001, 840), Dc(0.05, 300), Dc(0.001, 860));

        var r0 = SilenceTrimmer.Trim(buf, 0);
        var rn = SilenceTrimmer.Trim(buf, -100);

        rn.IsSilent.ShouldBe(r0.IsSilent);
        rn.VoicedMs.ShouldBe(r0.VoicedMs);
        rn.ClearVoicedMs.ShouldBe(r0.ClearVoicedMs);
        rn.RemovedMs.ShouldBe(r0.RemovedMs);
        rn.Trimmed.SequenceEqual(r0.Trimmed).ShouldBeTrue();
    }

    [Fact]
    public void Trim_CueBeepAloneInsideMask_NoLongerPassesEscapeHatch()
    {
        // THE fixed bug (confirmed escape 2026-08-02 20:18:00): a silent
        // recording where the only energy is the start cue's mic pickup.
        // 1500 ms buffer = 75 frames; a 240 ms 0.05-RMS "beep" at 520-760 ms
        // (frames 26-37, inside the 1000 ms mask). Unmasked: P90 idx
        // floor(0.9*74)=66 lands in the 12 loud frames -> P90=0.05 passes the
        // 0.004 gate; threshold max(3*0.001,0.002)=0.003; voiced 240 < 600
        // but clear 240 >= 100 -> the escape hatch PASSES a silent recording.
        var buf = Join(Dc(0.001, 520), Dc(0.05, 240), Dc(0.001, 740));

        SilenceTrimmer.Trim(buf, 0).IsSilent.ShouldBeFalse(); // the escape, pinned

        var masked = SilenceTrimmer.Trim(buf, 1000);
        // Masked decision set = frames 50-74, all 0.001 -> P90-silent path.
        masked.IsSilent.ShouldBeTrue();
        masked.VoicedMs.ShouldBe(0);
        masked.ClearVoicedMs.ShouldBe(0);           // beep no longer counted
        masked.MaxFrameRms.ShouldBe(0.001, 0.0005); // post-mask max, not 0.05
        masked.Trimmed.Length.ShouldBe(0);
        masked.RemovedMs.ShouldBe(0);
        masked.RunsTrimmed.ShouldBe(0);
    }

    [Fact]
    public void Trim_VoicedSpeechAfterMask_StillPasses_TrimmingUnchanged()
    {
        // 2000 ms = 100 frames: 1000 ms room tone | 700 ms speech | 300 ms tone.
        // Masked decision set = 50 frames (35 loud): P90 idx floor(0.9*49)=44
        // -> 0.05; threshold 0.003; voiced 700 >= 600 -> kept. Trimming runs
        // on ALL frames: leading 50-frame silence run keeps 30, removes 20
        // (400 ms); trailing 15 <= 30 kept whole.
        var buf = Join(Dc(0.001, 1000), Dc(0.05, 700), Dc(0.001, 300));

        var masked = SilenceTrimmer.Trim(buf, 1000);
        var unmasked = SilenceTrimmer.Trim(buf, 0);

        masked.IsSilent.ShouldBeFalse();
        masked.VoicedMs.ShouldBe(700);
        masked.ClearVoicedMs.ShouldBe(700);
        masked.RemovedMs.ShouldBe(400);
        masked.RunsTrimmed.ShouldBe(1);
        masked.Trimmed.Length.ShouldBe(80 * 320); // (100 - 20 removed) frames
        masked.Trimmed.SequenceEqual(unmasked.Trimmed).ShouldBeTrue();
    }

    [Fact]
    public void Trim_SpeechStartingInsideMask_PassesOnPostMaskRemainder()
    {
        // Speech spans 700-2100 ms — it STARTS inside the 1000 ms mask window.
        // 3000 ms = 150 frames: 35 tone | 70 speech (frames 35-104) | 45 tone.
        // Post-mask decision set = 100 frames with 55 speech frames: voiced
        // 1100 >= 600 -> kept. The 300 ms of speech inside the mask is
        // excluded from the COUNT (honest post-mask observability), not from
        // the transcribed audio.
        var buf = Join(Dc(0.001, 700), Dc(0.05, 1400), Dc(0.001, 900));

        var masked = SilenceTrimmer.Trim(buf, 1000);
        var unmasked = SilenceTrimmer.Trim(buf, 0);

        masked.IsSilent.ShouldBeFalse();
        masked.VoicedMs.ShouldBe(1100);      // 1400 total minus 300 in-mask
        masked.ClearVoicedMs.ShouldBe(1100);
        // Trimming identical to unmasked: leading 35-frame run removes 5
        // (100 ms), trailing 45-frame run removes 15 (300 ms).
        masked.RemovedMs.ShouldBe(400);
        masked.RunsTrimmed.ShouldBe(2);
        masked.Trimmed.SequenceEqual(unmasked.Trimmed).ShouldBeTrue();
    }

    [Fact]
    public void Trim_MaskDoesNotChangeTrimOffsets_InteriorGap()
    {
        // Trim-invariance headline: an interior-gap shape whose trimming must
        // be bit-identical with and without the mask. 5000 ms = 250 frames:
        // 50 tone | 30 speech | 145 tone | 25 speech. Trimming (all frames):
        // leading run removes 20 (400 ms), interior 145-frame run keeps
        // 2*30 and removes 85 (1700 ms) -> RemovedMs 2100, runs 2.
        var buf = Join(Dc(0.001, 1000), Dc(0.05, 600), Dc(0.001, 2900), Dc(0.05, 500));

        var masked = SilenceTrimmer.Trim(buf, 1000);
        var unmasked = SilenceTrimmer.Trim(buf, 0);

        masked.IsSilent.ShouldBeFalse();
        masked.VoicedMs.ShouldBe(1100); // both speech blocks sit after the mask
        masked.RemovedMs.ShouldBe(2100);
        masked.RunsTrimmed.ShouldBe(2);
        unmasked.RemovedMs.ShouldBe(2100);
        unmasked.RunsTrimmed.ShouldBe(2);
        masked.Trimmed.SequenceEqual(unmasked.Trimmed).ShouldBeTrue();
    }

    [Fact]
    public void Trim_RecordingShorterThanMask_IsSilent_DoesNotThrow()
    {
        // 800 ms buffer (40 frames) vs a 1000 ms mask: every frame is masked.
        // The naive implementation would run percentiles over an empty array
        // and throw on sorted[^1] — this pins the guard. Unmasked, the same
        // buffer passes via the clear tier (voiced 400 < 600, clear 400 >= 100).
        var buf = Join(Dc(0.001, 200), Dc(0.05, 400), Dc(0.001, 200));

        SilenceTrimmer.Trim(buf, 0).IsSilent.ShouldBeFalse();

        var masked = SilenceTrimmer.Trim(buf, 1000);
        masked.IsSilent.ShouldBeTrue();
        masked.VoicedMs.ShouldBe(0);
        masked.ClearVoicedMs.ShouldBe(0);
        masked.MaxFrameRms.ShouldBe(0.0);
        masked.Trimmed.Length.ShouldBe(0);
        masked.RemovedMs.ShouldBe(0);
        masked.RunsTrimmed.ShouldBe(0);
    }

    [Fact]
    public void Trim_UtteranceEntirelyInsideMask_IsSilent_KnownResidual()
    {
        // ACCEPTED RESIDUAL (intentional, decided 2026-08-02): a genuine
        // 500 ms utterance at 100-600 ms — entirely inside the ~1000 ms warm
        // mask window (the window shrinks with the actual pre-roll, to
        // ~500 ms cold) — is now classified silent. The mask window is
        // dominated by pre-hotkey pre-roll audio; the exact-semantics
        // validation over the frozen archive showed 0/91 real gate-passing
        // dictations flip pass->drop (and 0/6 drop->pass) at 1000 ms. Drops
        // remain non-destructive (the original audio is archived).
        // 1500 ms = 75 frames: 5 tone | 25 speech (frames 5-29) | 45 tone.
        var buf = Join(Dc(0.001, 100), Dc(0.05, 500), Dc(0.001, 900));

        SilenceTrimmer.Trim(buf, 0).IsSilent.ShouldBeFalse(); // passes unmasked

        var masked = SilenceTrimmer.Trim(buf, 1000);
        masked.IsSilent.ShouldBeTrue();
        masked.VoicedMs.ShouldBe(0);
        masked.ClearVoicedMs.ShouldBe(0);
    }

    [Fact]
    public void Trim_MaskRoundsUpToWholeFrames()
    {
        // ceil semantics: a 890 ms mask must cover frame 44 (880-900 ms) —
        // a mask's job is exclusion, so a partially covered frame is fully
        // excluded. 2000 ms = 100 frames with ONE loud frame at index 44.
        // Both runs take the P90-silent path (1 loud frame of 100); the
        // difference is whether that frame is counted as clear.
        var buf = Join(Dc(0.001, 880), Dc(0.05, 20), Dc(0.001, 1100));

        SilenceTrimmer.Trim(buf, 0).ClearVoicedMs.ShouldBe(20);   // counted today
        SilenceTrimmer.Trim(buf, 890).ClearVoicedMs.ShouldBe(0);  // ceil -> masked
    }
```

If `System.Linq` is not already available for `SequenceEqual` (implicit usings
include it; check the file's existing usings), add `using System.Linq;` to the top.

- [ ] **Step 2: Run the suite to verify the new tests fail**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask && ./scripts/linux-tests.sh
```
Expected: FAILURE — `Winpepper.Audio.Tests` does not build (`CS1501`: no overload
for method `Trim` takes 2 arguments).

- [ ] **Step 3: Implement the mask in `SilenceTrimmer.Trim`**

In `src/Winpepper.Audio/SilenceTrimmer.cs`, replace the ENTIRE `Trim` method
(currently `public static TrimResult Trim(ReadOnlySpan<float> samples)` through its
closing brace, lines 118–281 — everything between the `MinClearVoicedDurationMs`
constant and the `AppendKeep` helper) with exactly:

```csharp
    /// <summary>
    /// Trims silence and decides voice-presence for a finished session buffer.
    /// <paramref name="maskMs"/> excludes the leading start-cue window from the
    /// gate DECISION (the P90-silent gate, the decision threshold's P10/P90
    /// percentiles, and the VoicedMs/ClearVoicedMs/MaxFrameRms counting)
    /// WITHOUT touching the trim math: the trim threshold, isSilence[], the
    /// run walker, RemovedMs/RunsTrimmed, and the output buffer are computed
    /// over ALL frames exactly as before, so the transcribed audio and the
    /// trim accounting are mask-independent. maskMs = 0 (the default) is
    /// byte-identical to the pre-mask behavior. The caller computes maskMs
    /// from the actually-seeded pre-roll plus the runtime-measured cue
    /// duration — see <see cref="StartCueGateMask"/>. Partial mask frames round UP
    /// (a mask's job is exclusion). A mask covering every frame classifies
    /// the recording silent (accepted residual: an utterance entirely inside
    /// the mask window is dropped; drops stay non-destructive — the caller
    /// archives the original audio).
    /// </summary>
    public static TrimResult Trim(ReadOnlySpan<float> samples, int maskMs = 0)
    {
        var n = samples.Length;
        var frameCount = n / FrameSamples;
        if (frameCount == 0)
        {
            // Fewer than one full frame: nothing to analyze. Not "silent" —
            // empty/cancel captures are guarded by the caller's length check.
            return new TrimResult
            {
                Trimmed = samples.ToArray(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = false,
                VoicedMs = 0,
                ClearVoicedMs = 0,
                MaxFrameRms = 0,
            };
        }

        var rms = new double[frameCount];
        for (var f = 0; f < frameCount; f++)
            rms[f] = AudioEnergy.Rms(samples.Slice(f * FrameSamples, FrameSamples));

        // Start-cue mask: frames [0, maskFrames) are excluded from every
        // DECISION statistic below but stay in the buffer and in the trim
        // threshold. Ceil, so a partially covered frame is fully excluded.
        var maskFrames = maskMs <= 0 ? 0 : Math.Min((maskMs + FrameMs - 1) / FrameMs, frameCount);
        var decisionFrameCount = frameCount - maskFrames;

        if (decisionFrameCount == 0)
        {
            // Every frame sits inside the cue mask: no decision evidence
            // exists, so the recording is silent by definition. ACCEPTED
            // RESIDUAL: an utterance spoken entirely inside the mask window
            // is dropped (see Trim_UtteranceEntirelyInsideMask_IsSilent_
            // KnownResidual). This branch is also the guard that keeps the
            // percentile math off an empty array (sorted[^1] would throw).
            return new TrimResult
            {
                Trimmed = Array.Empty<float>(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = true,
                VoicedMs = 0,
                ClearVoicedMs = 0,
                MaxFrameRms = 0,
            };
        }

        // DECISION statistics run over post-mask frames only. With maskMs = 0
        // this is all frames and everything below matches the pre-mask code.
        var decisionSorted = new double[decisionFrameCount];
        Array.Copy(rms, maskFrames, decisionSorted, 0, decisionFrameCount);
        Array.Sort(decisionSorted);
        var speechLevel = Percentile(decisionSorted, SpeechLevelPercentile);

        if (speechLevel < SilentSpeechLevel)
        {
            // P90-silent: the adaptive threshold is undefined (it is derived
            // from a speech level that does not exist), so VoicedMs reports
            // 0. Clear/max fields are absolute and still meaningful -- they
            // keep long-recording transient near-misses diagnosable from the
            // drop log (the gate constants are recalibrated from these
            // fields). Counted post-mask, so the start cue can no longer
            // inflate the recalibration fields (pre-mask logs showed
            // clear=60-160 ms of pure beep on every silent drop).
            var clearMsAtP90 = 0;
            for (var f = maskFrames; f < frameCount; f++)
                if (rms[f] >= ClearSpeechRmsFloor) clearMsAtP90 += FrameMs;
            return new TrimResult
            {
                Trimmed = Array.Empty<float>(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = true,
                VoicedMs = 0,
                ClearVoicedMs = clearMsAtP90,
                MaxFrameRms = decisionSorted[^1],
            };
        }

        var noiseFloor = Percentile(decisionSorted, NoiseFloorPercentile);

        // Adaptive DECISION threshold based on the post-mask noise floor and
        // speech level (same formula as always; identical to the trim
        // threshold when maskMs = 0).
        var threshold = Math.Max(ThresholdNoiseMultiplier * noiseFloor, ThresholdAbsFloor);
        // Fail-safe: when the noise floor is high relative to speech, silence
        // cannot be confidently separated. Capping the threshold at a fraction
        // of speechLevel keeps genuine silence-vs-speech separable and makes
        // low-SNR recordings a no-op instead of eating real audio.
        threshold = Math.Min(threshold, SpeechCapFactor * speechLevel);

        // Minimum-voiced-duration gate (2026-07-28 transient-rejection fix;
        // AND semantics -- the owner-fixed P90 parameters above are not
        // re-derived, and this gate can only make the verdict MORE silent).
        // Counts post-mask frames only, so the start cue can no longer supply
        // voiced/clear milliseconds (2026-08-02: an unmasked cue alone could
        // satisfy the 100 ms clear tier and unlock a silent recording).
        var voicedMs = 0;
        var clearVoicedMs = 0;
        var maxFrameRms = 0.0;
        for (var f = maskFrames; f < frameCount; f++)
        {
            if (rms[f] > maxFrameRms) maxFrameRms = rms[f];
            if (rms[f] < threshold) continue;
            voicedMs += FrameMs;
            if (rms[f] >= ClearSpeechRmsFloor) clearVoicedMs += FrameMs;
        }

        if (voicedMs < MinVoicedDurationMs && clearVoicedMs < MinClearVoicedDurationMs)
        {
            return new TrimResult
            {
                Trimmed = Array.Empty<float>(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = true,
                VoicedMs = voicedMs,
                ClearVoicedMs = clearVoicedMs,
                MaxFrameRms = maxFrameRms,
            };
        }

        // TRIM threshold: ALL frames, exactly the pre-mask computation, so
        // the mask can never move trimming offsets or change the output
        // buffer. (Masking the percentile sample would shift the threshold
        // and with it isSilence[] -- the walker's sole input.) When
        // maskFrames == 0 the decision threshold IS the all-frames threshold,
        // so the extra sort is skipped.
        var trimThreshold = threshold;
        if (maskFrames > 0)
        {
            var trimSorted = (double[])rms.Clone();
            Array.Sort(trimSorted);
            var trimSpeechLevel = Percentile(trimSorted, SpeechLevelPercentile);
            var trimNoiseFloor = Percentile(trimSorted, NoiseFloorPercentile);
            trimThreshold = Math.Max(ThresholdNoiseMultiplier * trimNoiseFloor, ThresholdAbsFloor);
            trimThreshold = Math.Min(trimThreshold, SpeechCapFactor * trimSpeechLevel);
        }

        var isSilence = new bool[frameCount];
        for (var f = 0; f < frameCount; f++)
            isSilence[f] = rms[f] < trimThreshold;

        // Walk contiguous silence runs; build the ordered list of whole-frame
        // segments to KEEP. Interior runs keep 600 ms per speech edge; edge runs
        // keep 600 ms adjacent to their single speech edge; the middle is deleted.
        var kept = new List<(int start, int len)>();
        var removedFrames = 0;
        var runsTrimmed = 0;

        var i = 0;
        while (i < frameCount)
        {
            if (!isSilence[i])
            {
                AppendKeep(kept, i, 1);
                i++;
                continue;
            }

            var runStart = i;
            while (i < frameCount && isSilence[i]) i++;
            var runEnd = i; // exclusive
            var runLen = runEnd - runStart;

            var hasLeftSpeech = runStart > 0;
            var hasRightSpeech = runEnd < frameCount;
            var edges = (hasLeftSpeech ? 1 : 0) + (hasRightSpeech ? 1 : 0);
            var keepBudget = edges * KeepFramesPerEdge;

            if (edges > 0 && runLen > keepBudget)
            {
                if (hasLeftSpeech) AppendKeep(kept, runStart, KeepFramesPerEdge);
                if (hasRightSpeech) AppendKeep(kept, runEnd - KeepFramesPerEdge, KeepFramesPerEdge);
                removedFrames += runLen - keepBudget;
                runsTrimmed++;
            }
            else
            {
                // Short enough to keep whole, or an all-silence buffer with no
                // speech edge (defensive; the IsSilent gate normally catches it).
                AppendKeep(kept, runStart, runLen);
            }
        }

        var keptFrames = 0;
        foreach (var seg in kept) keptFrames += seg.len;
        var tail = n - frameCount * FrameSamples;
        var outBuf = new float[keptFrames * FrameSamples + tail];

        var w = 0;
        foreach (var (start, len) in kept)
        {
            samples.Slice(start * FrameSamples, len * FrameSamples).CopyTo(outBuf.AsSpan(w));
            w += len * FrameSamples;
        }
        if (tail > 0)
            samples.Slice(frameCount * FrameSamples, tail).CopyTo(outBuf.AsSpan(w));

        return new TrimResult
        {
            Trimmed = outBuf,
            RemovedMs = removedFrames * FrameMs,
            RunsTrimmed = runsTrimmed,
            IsSilent = false,
            VoicedMs = voicedMs,
            ClearVoicedMs = clearVoicedMs,
            MaxFrameRms = maxFrameRms,
        };
    }
```

Then make three doc-only edits to `TrimResult` in the same file (its XML docs must
stay honest about the mask):

1. On `VoicedMs`, replace the line
   `/// Milliseconds of voiced (above-adaptive-threshold) audio detected.`
   with
   `/// Milliseconds of voiced (above-adaptive-threshold) audio detected,`
   `/// counted over post-cue-mask frames only when a mask is supplied.`
2. On `ClearVoicedMs`, replace the line
   `/// Milliseconds of frames at or above ClearSpeechRmsFloor (0.02).`
   with
   `/// Milliseconds of frames at or above ClearSpeechRmsFloor (0.02),`
   `/// counted over post-cue-mask frames only when a mask is supplied.`
3. On `MaxFrameRms`, replace the line
   `/// Loudest 20 ms frame RMS observed (0 for sub-frame buffers).`
   with
   `/// Loudest 20 ms frame RMS observed outside the cue mask (0 for`
   `/// sub-frame and fully-masked buffers).`

- [ ] **Step 4: Run the suite to verify everything passes**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask && ./scripts/linux-tests.sh
```
Expected: PASS — exit 0. All 23 pre-existing SilenceTrimmer tests green UNCHANGED
(that is the mask=0 regression proof) plus the 9 new mask tests.

- [ ] **Step 5: Refresh the stale cross-file comments**

The edit shifts `SilenceTrimmer.cs` line numbers, and two other files cite them:

1. `src/Winpepper.Asr/InteriorSilenceSkipper.cs` — comments at lines 154, 184, 186
   contain `SilenceTrimmer.cs:NN-NN` line-range citations. Open the file, find the
   three citations, look up what they point at (the trim-run walker and keep-budget
   logic), and update each range to the code's new location in the edited
   `SilenceTrimmer.cs`. Comment-only change — no code.
2. `src/Winpepper.Asr/Transcription/ParakeetStreamingSession.cs` — the
   `LeadingSilenceRmsFloor` doc comment at `:61-73` documents a DELIBERATE
   DIVERGENCE from the batch gate. Append one sentence to that comment block:
   `// A second divergence since 2026-08-02: the batch gate additionally masks`
   `// the start-cue window out of its decision (SilenceTrimmer.Trim maskMs);`
   `// streaming has no gate and therefore no mask.`
   (Match the comment style actually used in that block — XML doc `///` or `//` —
   when inserting.)

Run `./scripts/linux-tests.sh` again. Expected: PASS (comment-only edits).

- [ ] **Step 6: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask
git add src/Winpepper.Audio/SilenceTrimmer.cs tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs \
        src/Winpepper.Asr/InteriorSilenceSkipper.cs src/Winpepper.Asr/Transcription/ParakeetStreamingSession.cs
git commit -m "$(cat <<'EOF'
feat(audio): SilenceTrimmer cue mask — exclude the masked head from the gate decision, trimming bit-identical

maskMs (optional, default 0 = today's behavior) removes head frames from
the P90-silent gate, the decision threshold's percentiles, and the
voiced/clear/max-RMS counting. The trim threshold and walker still run
over ALL frames, so offsets, trim_removed, and the transcribed buffer
never move. Fully-masked recordings are silent by definition (accepted
residual, pinned by test); partial mask frames round up.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 4: `ISoundEffectPlayer.StartCueMs` — the player reports its measured cue

**Files:**
- Modify: `src/Winpepper.Core/Audio/ISoundEffectPlayer.cs`
- Modify: `src/Winpepper.Core/Audio/NoopSoundEffectPlayer.cs`
- Modify: `src/Winpepper.App/Audio/WinUiSoundEffectPlayer.cs`
- Test: `tests/Winpepper.Core.Tests/Audio/NoopSoundEffectPlayerTests.cs`

**Interfaces:**
- Consumes: `Winpepper.Audio.WavDuration.TryMeasureMs(string, out int)` (Task 1).
- Produces: `int ISoundEffectPlayer.StartCueMs { get; }` — the measured duration of
  the start cue asset in ms, `0` when unknown/unmeasurable. `WinUiSoundEffectPlayer`
  measures the SAME file it plays (the `start.wav` path it hands to `SoundPlayer`),
  once, at construction. `NoopSoundEffectPlayer` returns `0`. Task 5 reads
  `_sounds.StartCueMs` and `_sounds.Enabled` from `PipelineHost`.

- [ ] **Step 1: Write the failing test**

In `tests/Winpepper.Core.Tests/Audio/NoopSoundEffectPlayerTests.cs`, add inside the
test class (match the file's existing construction style for the player):

```csharp
    [Fact]
    public void StartCueMs_IsZero()
    {
        // The no-op player emits no cue, so there is never anything to mask.
        new NoopSoundEffectPlayer().StartCueMs.ShouldBe(0);
    }
```

- [ ] **Step 2: Run the suite to verify it fails**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask && ./scripts/linux-tests.sh
```
Expected: FAILURE — `Winpepper.Core.Tests` does not build (`CS1061`:
`NoopSoundEffectPlayer` contains no definition for `StartCueMs`).

- [ ] **Step 3: Find every implementor before changing the interface**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask && grep -rn "ISoundEffectPlayer" --include="*.cs" src tests
```
Known implementors: `NoopSoundEffectPlayer` (Core) and `WinUiSoundEffectPlayer`
(App, `#if WINDOWS`). If the grep reveals any additional implementor (e.g. a test
fake), add `public int StartCueMs => 0;` to it in this same task — an interface
member addition breaks every implementor at compile time.

- [ ] **Step 4: Implement**

In `src/Winpepper.Core/Audio/ISoundEffectPlayer.cs`, add one member to the
interface (after `bool Enabled { get; set; }`):

```csharp
    /// <summary>
    /// Measured duration of the start-cue asset in milliseconds, read from
    /// the actual file this player plays; 0 when unknown (no-op player,
    /// missing or unparseable asset). Used by the silence gate to mask the
    /// cue's mic-pickup window out of its decision — see StartCueGateMask.
    /// </summary>
    int StartCueMs { get; }
```

In `src/Winpepper.Core/Audio/NoopSoundEffectPlayer.cs`, add to the class:

```csharp
    public int StartCueMs => 0;
```

In `src/Winpepper.App/Audio/WinUiSoundEffectPlayer.cs`, replace the constructor and
add the property, so the class body becomes (the file is 26 lines; `PlayStart`/
`PlayStop`/`Dispose` and the fields stay exactly as they are):

```csharp
    private readonly SoundPlayer _start;
    private readonly SoundPlayer _stop;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Measured once at construction from the SAME file handed to
    /// SoundPlayer, so the mask math can never disagree with what actually
    /// plays. 0 when the header is unreadable (WavDuration fails open).
    /// </summary>
    public int StartCueMs { get; }

    public WinUiSoundEffectPlayer(string assetsDir)
    {
        var startPath = Path.Combine(assetsDir, "start.wav");
        _start = new SoundPlayer(startPath);
        _stop  = new SoundPlayer(Path.Combine(assetsDir, "stop.wav"));
        _start.Load(); _stop.Load();
        StartCueMs = Winpepper.Audio.WavDuration.TryMeasureMs(startPath, out var cueMs) ? cueMs : 0;
    }
```

(`Winpepper.App` already references `Winpepper.Audio` — `PipelineHost` calls
`Winpepper.Audio.SilenceTrimmer` today — so the fully-qualified call needs no new
project reference.)

- [ ] **Step 5: Run the suite to verify it passes**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask && ./scripts/linux-tests.sh
```
Expected: PASS — exit 0. (`WinUiSoundEffectPlayer` is `#if WINDOWS` and is NOT
compiled by the Linux gate — it is compile-verified by the Windows gate in Task 6.)

- [ ] **Step 6: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask
git add src/Winpepper.Core/Audio/ISoundEffectPlayer.cs src/Winpepper.Core/Audio/NoopSoundEffectPlayer.cs \
        src/Winpepper.App/Audio/WinUiSoundEffectPlayer.cs tests/Winpepper.Core.Tests/Audio/NoopSoundEffectPlayerTests.cs
# plus any additional implementor files found in Step 3
git commit -m "$(cat <<'EOF'
feat(core): expose the runtime-measured start-cue duration on ISoundEffectPlayer

WinUiSoundEffectPlayer measures the same start.wav it plays, once at
construction, via WavDuration (0 on unreadable header — fail open);
NoopSoundEffectPlayer reports 0. The same object that owns Enabled now
owns the duration, so mask gating can never drift from what the player
actually did.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 5: recorder pre-roll return + `PipelineHost` wiring — mask the gate, log honestly

**Files:**
- Modify: `src/Winpepper.Audio/IWarmAudioRecorder.cs` (`StartSession` returns `int`)
- Modify: `src/Winpepper.Audio/WarmWasapiRecorder.cs` (`#if WINDOWS`; return the
  actually-seeded pre-roll ms)
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs`
  (lines cited below are pre-change positions at `7345ca1`)

**Interfaces:**
- Consumes: `StartCueGateMask.WarmPrerollMs` / `.ComputeMaskMs(int, int, bool)`
  (Task 2), `SilenceTrimmer.Trim(samples, maskMs)` (Task 3), `_sounds.StartCueMs` +
  `_sounds.Enabled` (Task 4; `_sounds` is the existing `ISoundEffectPlayer` field).
- Produces: `int IWarmAudioRecorder.StartSession(int includePrerollMs)` — returns
  the pre-roll milliseconds ACTUALLY seeded from the warm ring (0 cold/prewarm-off,
  less than requested when the ring is drained) — plus the production behavior:
  preroll-aware masked gate decisions, `cue mask NNN ms` on the silent-drop log
  line, one startup INF (or fail-open WRN) line. No test project references
  `Winpepper.App`; the `IWarmAudioRecorder` interface change compiles in
  `Winpepper.Audio`'s Linux-built `net9.0` TFM (proven by the Step 5 Linux run),
  while `WarmWasapiRecorder` (`#if WINDOWS`, the sole implementor — no test fakes
  exist) and `PipelineHost` are compile-verified by the Windows gate (Task 6) per
  the established house pattern ("pure decision logic in net9.0 projects with
  Linux tests; PipelineHost gets thin, compile-verified wiring").

Note: `PipelineHost.cs:4` already has `using Winpepper.Audio;` — the unqualified
names below resolve.

- [ ] **Step 1: Return the actually-seeded pre-roll from the recorder**

In `src/Winpepper.Audio/IWarmAudioRecorder.cs`, replace the `StartSession` member
(`:28-30`) with:

```csharp
    /// <summary>Begin a session, seeding up to <paramref name="includePrerollMs"/>
    /// milliseconds of already-captured audio. Returns the pre-roll milliseconds
    /// ACTUALLY seeded (0 when prewarm is off; less than requested when the ring
    /// was drained or cleared) — the silence-gate cue mask is sized from this
    /// value, never from the request (see StartCueGateMask).</summary>
    int StartSession(int includePrerollMs);
```

In `src/Winpepper.Audio/WarmWasapiRecorder.cs`, change the implementation's
signature (`:127`) from `public void StartSession(int includePrerollMs)` to
`public int StartSession(int includePrerollMs)` and add a return at the END of the
method body (after the existing `if (preroll.Length > 0) FramesAvailable?.Invoke(preroll);`
at `:147` — the method body is otherwise unchanged):

```csharp
        // Report the pre-roll ACTUALLY seeded: prewarm-off yields 0, a
        // drained/cleared ring yields less than requested (the mask window
        // shrinks with it — see StartCueGateMask). Integer division floors
        // (the ring count is not frame-aligned), under-reporting by <1 ms,
        // which SilenceTrimmer's ceil frame rounding absorbs.
        return preroll.Length / (SampleRate16k / 1000);
```

Then run `grep -rn "IWarmAudioRecorder" --include="*.cs" src tests scripts` — the
only implementor is `WarmWasapiRecorder` and the only consumer field is
`PipelineHost.cs:42`; if the grep reveals any new implementor or fake, update it
in this same step (the return-type change breaks it at compile time).

- [ ] **Step 2: Single-source the pre-roll literal and capture the return (both hotkey arms)**

Add a private field next to the `_warmRecorder` field (`PipelineHost.cs:42`):

```csharp
    /// <summary>
    /// Pre-roll milliseconds the recorder ACTUALLY seeded into the current
    /// session (StartSession's return): 0 when prewarm is off, less than the
    /// WarmPrerollMs request when the ring was drained/cleared. Sizes the
    /// per-dictation silence-gate cue mask (StartCueGateMask.ComputeMaskMs).
    /// Sessions are serialized by the engine state machine, so one field
    /// suffices — but BOTH hotkey arms (hold + toggle) MUST assign it;
    /// missing one arm silently reuses the other arm's stale value (no
    /// compile error catches it). The cancel path leaves it stale, which is
    /// benign: the next StartSession overwrites it before any trim reads it.
    /// </summary>
    private int _lastSessionPrerollMs;
```

At `PipelineHost.cs:543` (hold arm) and `:1119` (toggle arm), replace

```csharp
                _warmRecorder!.StartSession(includePrerollMs: 500);
```

with

```csharp
                _lastSessionPrerollMs = _warmRecorder!.StartSession(includePrerollMs: StartCueGateMask.WarmPrerollMs);
```

(Preserve each site's exact leading indentation, and make sure BOTH arms assign
the field — see the field's doc comment.) Then check the nearby prose comments at
`:502` and `:1084` that reference the pre-roll (the `:502` one names the
500 ms figure; the `:1084` one describes the pre-roll behavior) — reword each to
"the StartCueGateMask.WarmPrerollMs (500 ms) pre-roll request" so the constant is
discoverable from the call sites.

- [ ] **Step 3: Add the startup observability lines**

At the END of the `PipelineHost` constructor body (after all existing field
assignments — `_log` and `_sounds` are assigned by then; the ctor signature starts
at `PipelineHost.cs:99`), add:

```csharp
        // Startup observability for the silence-gate cue mask (2026-08-02):
        // one honest line stating what was measured and the WORST-CASE warm
        // mask, so recalibration reads of the drop log know the counting
        // basis. The mask itself varies per-dictation with the pre-roll the
        // recorder actually seeded (see TrimForTranscription); the actual
        // value is logged on each silent-drop line.
        var startCueMs = sounds.StartCueMs;
        if (startCueMs > 0)
            _log.LogInformation(
                "start cue measured {CueMs} ms; worst-case warm silence-gate cue mask {WorstCaseMaskMs} ms (preroll request {PrerollMs} + start latency {LatencyMs} + cue + decay {DecayMs}; per-dictation mask uses the actually-seeded preroll; sounds enabled {Enabled})",
                startCueMs,
                StartCueGateMask.ComputeMaskMs(StartCueGateMask.WarmPrerollMs, startCueMs, sounds.Enabled),
                StartCueGateMask.WarmPrerollMs,
                StartCueGateMask.CueStartLatencyMarginMs,
                StartCueGateMask.CueDecayMarginMs,
                sounds.Enabled);
        else
            _log.LogWarning(
                "start cue duration unavailable (missing or unparseable start.wav); silence-gate cue mask disabled — gate behaves as before (fail open)");
```

(If the ctor assigns the parameter to `_sounds`, using `sounds` or `_sounds` here is
equivalent; keep whichever reads consistently with the surrounding code.)

- [ ] **Step 4: Mask the gate decision and extend the drop line**

In `TrimForTranscription` (`PipelineHost.cs:1668-1690`), replace the method body's
first line and the drop-log statement. The method currently reads:

```csharp
    private float[]? TrimForTranscription(float[] samples, Guid sessionId, out int removedMs)
    {
        var result = Winpepper.Audio.SilenceTrimmer.Trim(samples);
        removedMs = result.RemovedMs;
        if (result.IsSilent)
        {
            var ms = (int)((long)samples.Length * 1000 / 16000);
            // voiced/clear/max-RMS make the provisional gate constants
            // recalibratable from logs and a dropped short utterance
            // diagnosable after the fact. Content-free: numbers only.
            _log.LogInformation(
                "dropped silent recording, {Ms} ms (voiced {VoicedMs} ms, clear {ClearVoicedMs} ms, max frame rms {MaxFrameRms:0.0000})",
                ms, result.VoicedMs, result.ClearVoicedMs, result.MaxFrameRms);
            return null;
        }
```

Change it to:

```csharp
    private float[]? TrimForTranscription(float[] samples, Guid sessionId, out int removedMs)
    {
        // Mask the app's own start cue out of the gate DECISION. Gated on the
        // player's actual Enabled state (NOT a settings snapshot: PlaySounds
        // is applied to the player once at boot, so the player is the single
        // honest source of whether a cue was emitted) and sized from the
        // pre-roll the recorder ACTUALLY seeded this session (NOT the
        // worst-case request: prewarm-off/drained-ring sessions shrink the
        // window instead of eating post-hotkey speech). Trimming offsets and
        // the transcribed audio are unaffected by the mask by construction.
        var cueMaskMs = StartCueGateMask.ComputeMaskMs(_lastSessionPrerollMs, _sounds.StartCueMs, _sounds.Enabled);
        var result = Winpepper.Audio.SilenceTrimmer.Trim(samples, cueMaskMs);
        removedMs = result.RemovedMs;
        if (result.IsSilent)
        {
            var ms = (int)((long)samples.Length * 1000 / 16000);
            // voiced/clear/max-RMS make the provisional gate constants
            // recalibratable from logs and a dropped short utterance
            // diagnosable after the fact. Content-free: numbers only.
            // Since 2026-08-02 these are POST-MASK counts — cue mask is
            // logged alongside so recalibration reads stay honest.
            _log.LogInformation(
                "dropped silent recording, {Ms} ms (voiced {VoicedMs} ms, clear {ClearVoicedMs} ms, max frame rms {MaxFrameRms:0.0000}, cue mask {CueMaskMs} ms)",
                ms, result.VoicedMs, result.ClearVoicedMs, result.MaxFrameRms, cueMaskMs);
            return null;
        }
```

The rest of the method (`trimmed silence:` log + `return result.Trimmed;`) stays
unchanged. Also extend the method's `<summary>` doc comment with one sentence:
`/// The silence-gate decision masks the start-cue window (StartCueGateMask);`
`/// the drop line's voiced/clear/max-RMS are post-mask counts.`

- [ ] **Step 5: Run the Linux suite (proves the shared projects still build/pass)**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask && ./scripts/linux-tests.sh
```
Expected: PASS — exit 0. This run also compiles the changed
`IWarmAudioRecorder.cs` in `Winpepper.Audio`'s `net9.0` TFM (the interface has no
Linux-compiled implementor, so no behavior test exists on Linux).
(`WarmWasapiRecorder` and `PipelineHost` are `#if WINDOWS`/Windows-only and
compile only in the Windows gate; this run proves nothing shared broke, per
AGENTS.md.)

- [ ] **Step 6: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask
git add src/Winpepper.Audio/IWarmAudioRecorder.cs src/Winpepper.Audio/WarmWasapiRecorder.cs \
        src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "$(cat <<'EOF'
feat(app): mask the start cue out of the silence-gate decision with a preroll-aware window

Per-dictation mask = StartCueGateMask.ComputeMaskMs(preroll the recorder
ACTUALLY seeded, player's measured cue, player's actual Enabled state) —
never a hardcoded duration, zero when sounds are off or the asset is
unmeasurable (fail open). IWarmAudioRecorder.StartSession now returns
the actually-seeded preroll ms so prewarm-off/drained-ring sessions
shrink the window (cold simulation: preroll-aware flips 0/91 real
dictations vs 4/91 for a fixed worst-case window). Both StartSession
sites reference StartCueGateMask.WarmPrerollMs instead of a duplicated
500 literal and BOTH capture the return. Drop line gains "cue mask NNN
ms" because its voiced/clear/max-RMS recalibration fields are now
post-mask counts; startup logs the measured cue + worst-case warm mask
(WRN when unmeasurable).

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 6: Full Windows gate + evidence-doc section

**Files:**
- Modify: `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md` (append one section)

**Interfaces:**
- Consumes: the completed Tasks 1–5 (the gate compiles `Winpepper.App` and the
  `#if WINDOWS` half of `Winpepper.Audio`, i.e. the Task 4 `WinUiSoundEffectPlayer`
  and Task 5 `WarmWasapiRecorder` + `PipelineHost` changes, for the first time)
  and both gate scripts.
- Produces: the recorded gate results + the spec-mandated investigation summary in
  the evidence doc.

- [ ] **Step 1: Run the full Windows gate**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask && ./scripts/windows-gate.sh
```
Expected: `GATE: GREEN`, exit 0. UNC MSB4025 and vsock interop flakes are known
transients — on such a failure, re-run up to 3 times before treating it as real.
If the gate reveals a genuine compile error in the `#if WINDOWS` code from Task 4
or Task 5 (the Linux gate cannot compile those files), fix it now, run
`./scripts/linux-tests.sh` (must be green), commit the fix as
`fix(app): <what was wrong>` with the standard trailer, and re-run the gate.

- [ ] **Step 2: Append the evidence section**

Append to the end of `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md`
(house style: `##` heading with em-dash + ISO date, flat `-` bullets wrapped ~72
cols, `file.cs:NN` pointers, SHOUTED verdicts, a gates bullet with exact counts).
The two `<record ...>` markers are run-result recorders — replace them with the
actual numbers/output from Step 1 and from the final Linux run; do not commit the
markers themselves:

```markdown

## Start-cue gate mask — 2026-08-02 escape investigation summary (2026-08-02)

- Investigation artifacts: `/tmp/gate-inv/` (exact Python replica of the
  gate pinned to `7345ca1`, frozen 100-recording snapshot, two days of
  app logs). Base for the fix branch: `main @ 7345ca1`.
- The app's start cue (`src/Winpepper.App/Assets/start.wav`, 150 ms,
  440->660 Hz at 22050 Hz) is picked up by the mic at ~592-861 ms into
  every fully-seeded warm buffer — recording starts with a ~500 ms
  retroactive warm pre-roll, and the cue becomes audible 92-144 ms
  after the hotkey (measured over 98 archive recordings, two
  independent detectors; the investigation's single ~20 ms latency
  observation was falsified) — reaching frame RMS up to 0.05, above
  the gate's 0.02 clear-speech tier.
- CONFIRMED escape: one silent recording passed the clear-speech escape
  hatch on the beep alone (2026-08-02 20:18:00, voiced=360 ms,
  clear=120 ms — the 120 ms was entirely beep). Every silent drop logged
  clear=60-160 ms, destroying the calibration margin that assumed
  non-speech recordings have at most one frame >= 0.02; short
  recordings also had their P90 statistic inflated.
- Two other archive escapes are the documented accepted residuals —
  sustained rustle >= 600 ms voiced
  (`Trim_SustainedQuietTransient_IsKept_KnownResidual`) and a desk
  thump through the escape hatch — NOT fixed by this change. Possible
  future fix recorded: spectral/periodicity voicedness feature (out of
  scope here).
- The beep-contamination class IS fixed: `SilenceTrimmer.Trim` gained a
  cue-mask parameter that excludes the head window from the gate
  DECISION only (`src/Winpepper.Audio/SilenceTrimmer.cs`); trimming
  offsets and the transcribed audio are bit-identical by construction.
  Window = 500 pre-roll + 200 start-latency + runtime-measured cue +
  150 decay (`src/Winpepper.Audio/StartCueGateMask.cs`), and it is
  preroll-aware: the pre-roll the recorder ACTUALLY seeded
  (`IWarmAudioRecorder.StartSession`'s return) replaces the 500 when
  shorter (0 in cold mode => ~500 ms window). The cue length is read
  from the shipped WAV header at startup
  (`src/Winpepper.Audio/WavDuration.cs`, measured by
  `src/Winpepper.App/Audio/WinUiSoundEffectPlayer.cs`) — NEVER
  hardcoded. PlaySounds off or unmeasurable asset => mask 0 (fail
  open). New accepted residual, tested + intentional: an utterance
  ENTIRELY inside the mask window (~1000 ms warm, shrinking with the
  actual pre-roll to ~500 ms cold) is classified silent
  (`Trim_UtteranceEntirelyInsideMask_IsSilent_KnownResidual`).
- Gate constants UNCHANGED (P90 0.004, 600 ms voiced, 100 ms @ 0.02
  hatch): the measured tuning cost of raising them was 23-41% of real
  dictations. Masking instead was re-validated with the plan's EXACT
  dual-threshold semantics over the frozen 100-recording archive
  (2026-08-02/03): 0/91 real pass->drop and 0/6 drop->pass at the
  1000 ms warm window (tightest passer margin 140 ms); cold-mode
  simulation: the preroll-aware mask flips 0/91 vs 4/91 for a fixed
  worst-case window.
- Load-bearing validation evidence (cue acoustics, exact-semantics
  replica runs, recorder API inspection):
  `/home/dan/code/winpepper/.worktrees/.the-usual-logs/start-cue-gate-mask/`
  (`load-bearing-ledger.md` + `reports/`).
- Observability: the silent-drop line now ends with `cue mask NNN ms`
  and its voiced/clear/max-RMS fields are post-mask counts
  (`PipelineHost.cs`, TrimForTranscription); startup logs one INF with
  the measured cue duration, the margin constants, and the WORST-CASE
  warm mask — the per-dictation mask uses the actually-seeded pre-roll
  (WRN fail-open line when the asset is unmeasurable).
- Gates: `scripts/linux-tests.sh` GREEN (<record exact total test
  count>, 9/9 projects); `scripts/windows-gate.sh` <record GATE: GREEN
  and how many attempts, noting any MSB4025/vsock transient retries>.
```

- [ ] **Step 3: Run the Linux suite and commit**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask && ./scripts/linux-tests.sh
```
Expected: PASS — exit 0. Then:

```bash
cd /home/dan/code/winpepper/.worktrees/start-cue-gate-mask
git add docs/plans/2026-07-29-cleanup-asr-contention-evidence.md
git commit -m "$(cat <<'EOF'
docs(plans): evidence — start-cue gate mask, 2026-08-02 escape investigation summary + gates

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

Do NOT push. The branch stays local for the root session to review, merge, gate,
and install.
