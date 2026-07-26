# Nemotron Streaming (transcribe.cpp) Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Real local streaming transcription — with streaming enabled and the new
engine installed, text is essentially ready when the user releases the hotkey
(post-stop ~100–300 ms vs today's ~3–4 s local batch), at batch-equal quality.

**Architecture:** A hand-rolled P/Invoke binding for transcribe.cpp v0.1.3
(ggml-based STT) lives in `Winpepper.Asr` behind a small interface
(`ITranscribeCppEngine`) so all session logic is Linux-testable with fakes. The
NVIDIA nemotron-speech-streaming-en-0.6b Q8_0 GGUF + the native runtime tarball
are acquired through the existing `ModelRegistry`/`ModelDownloader` machinery
(URL + SHA-256 pinned; tarball gains a tiny tar.gz extraction step). A new
`NemotronStreamingTranscriber` implements the existing `IStreamingTranscriber`
seam; `StreamingDictationSession`, batch fallback, `OrphanedPumpGuard`, and raw
audio archival are all preserved unchanged. The Parakeet-TDT ONNX batch path
(`ParakeetSession`) stays completely untouched as default and fallback.

**Tech Stack:** C#/.NET 9, P/Invoke (Cdecl, UTF-8, explicit struct layouts),
transcribe.cpp v0.1.3 (MIT), nemotron-speech-streaming-en-0.6b Q8_0 GGUF
(NVIDIA Open Model License), `System.Formats.Tar`, WinUI 3 (Models page), xunit.

## Global Constraints

- Worktree: `/home/dan/code/winpepper/.worktrees/nemotron-streaming` (branch `feat/nemotron-streaming`). All paths below are relative to this worktree unless absolute.
- `./scripts/linux-tests.sh` must print `LINUX SUITE: GREEN` before **every** commit. Native-DLL-dependent code must never execute in Linux tests — session logic is tested via fakes (the existing `FakeParakeetBackend` pattern).
- `./scripts/windows-gate.sh` is THE pre-push gate (Task 10). Run it FOREGROUND with a 45-minute timeout. Before any Windows gate/bench run, poll until no `dotnet.exe` with `winpepper` in its command line exists on the host: two consecutive zero-count polls 45 s apart (exact command in Task 9 Step 1).
- `powershell.exe` interop from WSL is FOREGROUND ONLY — never `nohup`/background it (vsock breaks). Windows dotnet is 9.0.316; Linux SDK is `/home/dan/code/winpepper/.dotnet` (9.0.100).
- Never install anything system-wide on the host. Never write to `C:\Users\dan\AppData\Local\winpepper` (read-only OK). Never launch or kill the user's running `Winpepper.exe`.
- Large downloads (~730 MB model + ~26 MB tarball) go to `%TEMP%` or the worktree; give bash calls explicit generous timeouts.
- Pin transcribe.cpp **v0.1.3 exactly**: native version string `0.1.3`, contract.json `header_hash` `86b16dd97ad1cb58`. CPU backend only (`backend = 1`); Vulkan is NOT used (spike measured ~16 s shader-compile warm-up + 4.7 s finalize).
- Default streaming lookahead `att_context_right = 13` (1040 ms). Audio is always 16 kHz mono float32 in [-1, 1].
- Verified ABI struct sizes (x86-64): `model_load_params`=16, `stream_params`=24, `capabilities`=56, `stream_update`=48, `stream_text`=64, `parakeet_stream_ext`=24 (offset of `att_context_right` is **16**). Every marshaled struct must be verified via `transcribe_abi_struct_size()` at engine init; PKST has no ABI id — assert via `[StructLayout(Size = 24)]` + a managed `Marshal.SizeOf` test.
- Text pointers returned by `transcribe_stream_get_text` are invalidated by every feed/finalize — copy with `Marshal.PtrToStringUTF8` immediately. A native session is single-threaded. Keep the log callback delegate alive statically.
- **One compute in flight per model (verified against v0.1.3 transcribe.h:11-20):** transcribe.cpp 0.x does NOT support concurrent compute — "at most one transcribe_run / transcribe_run_batch / active stream may be in flight across ALL sessions of a given model"; overlap produces corrupted decodes. The header's sanctioned fix: "Serialized use of many sessions on one model … is fully supported." The engine therefore owns a compute gate: `BeginStream` holds it for the stream's entire lifetime (released on stream dispose), `TranscribeBatch` holds it for the call (Task 4). The final transcript is always read from `full_text` (authoritative); `committed_text` is best-effort partials.
- **Session calls can race by design (verified in StreamingDictationSession.cs:126-138 + PipelineHost):** the pipeline DISPOSES a seam session as a concurrent abort while `PushAsync`/`FinishAsync` may be in flight (cancel, silence-drop, drain-timeout, teardown). The Nemotron session must serialize all native access under a session-level lock and make post-dispose calls harmless (Task 5).
- **VC++ redistributable prerequisite (verified by PE import dump of the real transcribe.dll):** transcribe.dll statically imports MSVCP140.dll / VCRUNTIME140.dll / VCRUNTIME140_1.dll — NOT in the tarball, NOT OS-inbox, NOT shipped by the app. On machines without the VC++ 2015–2022 x64 redist the engine load fails; the holder latches to batch (graceful), and the load-error message + README must name the prerequisite (Tasks 4/10). Same-dir statics (ggml.dll, ggml-base.dll) resolve fine: `NativeLibrary.Load(<abs path>)` uses altered search; the ggml backend DLLs are dynamically loaded (GGML_BACKEND_DL confirmed).
- Do NOT bundle the model or runtime into the MSI (`packaging/winpepper.wxs` stays untouched).
- Commit style: conventional commits, footer:
  ```
  Generated with [Amplifier](https://github.com/microsoft/amplifier)

  Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
  ```
- Nothing is pushed; the branch stays unmerged for user review.
- README.md is the only end-user markdown doc; `docs/plans/*` are working/agent docs. `THIRD-PARTY-NOTICES.md` is explicitly requested (Task 10).
- Reference material: proven spike code at `/home/dan/code/winpepper/artifacts/transcribe-spike-src/Program.cs` (read it — every marshaling fact below is proven there); v0.1.3 headers at `/tmp/t013/transcribe.h` + `/tmp/t013/parakeet.h` (if missing, re-fetch: `mkdir -p /tmp/t013 && curl -sL https://raw.githubusercontent.com/handy-computer/transcribe.cpp/v0.1.3/include/transcribe.h -o /tmp/t013/transcribe.h && curl -sL https://raw.githubusercontent.com/handy-computer/transcribe.cpp/v0.1.3/include/transcribe/parakeet.h -o /tmp/t013/parakeet.h`; note parakeet.h lives under `include/transcribe/`). Windows spike scratch (reusable for bench, read-only): `C:\Users\dan\AppData\Local\Temp\transcribe-spike\` containing `nemotron-speech-streaming-en-0.6b-Q8_0.gguf`, `transcribe-native.tar.gz`, extracted dir `transcribe-native-windows-x86_64-cpu-vulkan\`, and test WAVs.

### Pinned acquisition facts (verified 2026-07-25 against source APIs AND local sha256sum of the spike's files — do not re-derive)

| artifact | value |
|---|---|
| GGUF URL | `https://huggingface.co/handy-computer/nemotron-speech-streaming-en-0.6b-gguf/resolve/main/nemotron-speech-streaming-en-0.6b-Q8_0.gguf` |
| GGUF SHA-256 | `90d8c89714cd31efc88be62a40c6b2bea57e0cc2063af1ffe2c28f1a228ca110` |
| GGUF size | `729_650_176` bytes |
| Runtime URL | `https://github.com/handy-computer/transcribe.cpp/releases/download/v0.1.3/transcribe-native-0.1.3-windows-x86_64-cpu-vulkan.tar.gz` |
| Runtime SHA-256 | `9f536cb0fb839bd305e6d92fb214fd417c7718a416a6c7646a9911fbd56fdad5` |
| Runtime size | `25_957_910` bytes |
| Tarball layout | **one top-level dir** `transcribe-native-windows-x86_64-cpu-vulkan/` containing `transcribe.dll`, `ggml*.dll` (12), `contract.json`, `licenses/` |
| Model license | NVIDIA Open Model License (`license: other`, `license_name: nvidia-open-model-license`, link `https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-open-model-license/`) |

### Documented planner decisions (spec grants discretion; reviewers: these are intentional)

1. **`StreamingEnabled` default flips to `true`.** Safe because: (a) cloud
   streaming already has full `FallbackStreamingTranscriber` safety; (b) local
   WITH the nemotron model streams via transcribe.cpp with batch fallback on any
   failure; (c) local WITHOUT the model now gets `BatchStreamingAdapter` (see
   decision 2), which is behaviorally identical to today's default-OFF batch
   path.
2. **The local no-nemotron streaming branch returns
   `BatchStreamingAdapter(localBatch)` instead of the chunked-TDT
   `ParakeetStreamingTranscriber`.** The chunked-TDT path is proven unable to
   stream (blank-collapse; docs/plans/2026-07-25-streaming-verification-evidence.md)
   — it burns CPU on a doomed attempt and carries a documented residual
   false-negative risk in its guard (commit d6235ca). Eagerly using the batch
   adapter IS the "batch fallback makes it safe" posture, minus the wasted
   attempt. The ONNX batch path is untouched; `ParakeetStreamingTranscriber`
   stays in the codebase (bench `real-local` scenario + its 14 session tests).
3. **The runtime tarball is a second `ModelFile` on the nemotron descriptor**
   (not a new "runtime" concept). The archive file is kept on disk after
   extraction so `IsFullyInstalled`/hash verification keep working; extraction
   is idempotent via a `<archive>.extracted` marker containing the archive's
   SHA-256.
4. **New `ModelKind.StreamingAsr`** so the nemotron model never appears in the
   batch-ASR model combo (selecting it as `AsrModelName` would break
   `ParakeetSession`). It gets its own Models-page card with its own install
   button.
5. **Engine lifetime:** the model (~0.9 s load) is loaded lazily once per
   process by `NemotronEngineHolder` and never freed (no dispose race, no
   `OrphanedPumpGuard` changes). Each dictation gets its own native *session*
   (created in `BeginStream`, freed on stream dispose) — and, per the v0.1.3
   header contract, at most ONE compute may be in flight per model, so the
   engine serializes stream lifetimes and batch runs behind a compute gate
   (Task 4). Memory honesty (load-bearing review, 2026-07-25): once loaded,
   ~1 GB stays resident until the app exits. Handy — the tool whose engine we
   reuse — shipped exactly this posture for months, then moved to a 5-minute
   idle unload by default (PR #1051). v1 deliberately keeps never-free (an
   unload path would reintroduce the dispose races this design avoids and the
   model is opt-in-install); the residency is stated honestly in the model
   card caption and README, and idle-unload is the recorded follow-up.
6. **Performance posture on weaker hardware (load-bearing review, 2026-07-25):**
   the dev-host feed RTF is 0.112, but field evidence (Handy issue #1754,
   maintainer) puts low-end laptops at only 1–2x real time. There is no
   runtime perf gate by design: exposure is limited to users who explicitly
   install the streaming model, and the full batch-fallback guard
   (`was_truncated`/failure ⇒ TDT batch) is the safety valve — a slow machine
   gets today's batch behavior (plus wasted stream cost), never a corrupt
   transcript. The bench (Task 9) records real feed/finalize numbers; if the
   evidence shows the guard firing on the dev host, that is an acceptance
   failure, not a tuning knob.
7. **Fallback visibility asymmetry (intentional):** the cloud streaming path
   notifies users of fallback via `onFallback` (a toast with cloud-specific
   wording — `FallbackStreamingTranscriber.cs:129`, `PipelineHost.cs:443-447`).
   The local nemotron→batch fallback only logs a loud warning: the user still
   gets a correct local transcript, just slower, and the existing toast text
   would be wrong for it. The new local branch passes but never invokes
   `onFallback`.

## File Structure

```
src/Winpepper.Asr/TranscribeCpp/
  TranscribeCppNative.cs          # P/Invoke + explicit-layout structs + constants (internal)
  TranscribeCppContract.cs        # contract.json parse + version/header-hash gate (pure managed)
  ITranscribeCppEngine.cs         # ITranscribeCppEngine / ITranscribeCppStream / TranscribeCppException
  TranscribeCppEngine.cs          # real engine: resolver, init_backends, ABI gate, model, sessions, batch
  NemotronStreamingModel.cs       # path/name constants + IsInstalled(modelsRoot)
src/Winpepper.Asr/Transcription/
  NemotronStreamingTranscriber.cs # IStreamingTranscriber + session (buffering, fallback semantics)
src/Winpepper.Models/
  ModelFile.cs                    # + ExtractToRelative
  ModelKind.cs                    # + StreamingAsr
  ModelRegistry.cs                # + nemotron-streaming-en descriptor
  TarGzExtractor.cs               # System.Formats.Tar extraction + marker idempotence
  ModelDownloader.cs              # + EnsureExtracted hook
  ViewModels/ModelsTabViewModel.cs# + StreamingCard
src/Winpepper.App/
  Hosting/NemotronEngineHolder.cs # lazy process-wide engine
  Hosting/AppShell.cs             # BuildStreamingTranscriber local branch
  Views/ModelsPage.xaml{,.cs}     # streaming model card + honest captions
src/Winpepper.Core/Settings/AppSettings.cs  # StreamingEnabled default -> true
scripts/asr-latency-bench/Program.cs        # + real-nemotron-stream scenario
scripts/run-nemotron-bench-windows.sh       # new bench driver
scripts/verify-model-hashes.ps1             # + 2 new URLs
docs/plans/2026-07-25-nemotron-streaming-evidence.md  # REAL numbers (Task 9)
THIRD-PARTY-NOTICES.md                      # transcribe.cpp MIT + ggml + model license note
README.md                                   # model section update
```

---

### Task 1: Native binding — structs, P/Invoke, contract validation

**Files:**
- Create: `src/Winpepper.Asr/TranscribeCpp/TranscribeCppNative.cs`
- Create: `src/Winpepper.Asr/TranscribeCpp/TranscribeCppContract.cs`
- Test: `tests/Winpepper.Asr.Tests/TranscribeCpp/TranscribeCppStructLayoutTests.cs`
- Test: `tests/Winpepper.Asr.Tests/TranscribeCpp/TranscribeCppContractTests.cs`

**Interfaces:**
- Consumes: nothing (leaf module). Reference `/home/dan/code/winpepper/artifacts/transcribe-spike-src/Program.cs` and `/tmp/t013/transcribe.h`.
- Produces: `internal static class Winpepper.Asr.TranscribeCpp.TranscribeCppNative` (structs `ModelLoadParams`, `StreamParams`, `StreamUpdate`, `StreamText`, `Capabilities`, `ParakeetStreamExt`; all `DllImport`s; helpers `Str(IntPtr)`, `Status(int)`); `public sealed record TranscribeCppContract(string Version, string HeaderHash)` with `static TranscribeCppContract Parse(string json)`, `bool IsCompatible`, consts `RequiredVersion = "0.1.3"`, `RequiredHeaderHash = "86b16dd97ad1cb58"`.

- [ ] **Step 1: Write the failing struct-layout tests**

`Winpepper.Asr` is plain `net9.0` with no TFM conditionals — `DllImport`
declarations compile and `Marshal.SizeOf`/`OffsetOf` run fine on Linux; only
*calling* an import would fail. These tests lock the ABI facts the spike proved
at runtime.

```csharp
// tests/Winpepper.Asr.Tests/TranscribeCpp/TranscribeCppStructLayoutTests.cs
using System.Runtime.InteropServices;
using Winpepper.Asr.TranscribeCpp;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp;

public class TranscribeCppStructLayoutTests
{
    // Sizes runtime-verified against transcribe_abi_struct_size() by the spike
    // (artifacts/transcribe-spike-src). If any of these change, the native ABI
    // gate in TranscribeCppEngine would also fail — keep both in sync.
    [Fact] public void ModelLoadParams_is_16_bytes() => Assert.Equal(16, Marshal.SizeOf<TranscribeCppNative.ModelLoadParams>());
    [Fact] public void StreamParams_is_24_bytes() => Assert.Equal(24, Marshal.SizeOf<TranscribeCppNative.StreamParams>());
    [Fact] public void Capabilities_is_56_bytes() => Assert.Equal(56, Marshal.SizeOf<TranscribeCppNative.Capabilities>());
    [Fact] public void StreamUpdate_is_48_bytes() => Assert.Equal(48, Marshal.SizeOf<TranscribeCppNative.StreamUpdate>());
    [Fact] public void StreamText_is_64_bytes() => Assert.Equal(64, Marshal.SizeOf<TranscribeCppNative.StreamText>());
    [Fact] public void ParakeetStreamExt_is_24_bytes() => Assert.Equal(24, Marshal.SizeOf<TranscribeCppNative.ParakeetStreamExt>());

    // The load-bearing offset: transcribe_ext is {u64,u32} tail-padded to 16,
    // so att_context_right sits at byte 16 (proven in the spike).
    [Fact]
    public void ParakeetStreamExt_att_context_right_is_at_offset_16()
        => Assert.Equal(16, (int)Marshal.OffsetOf<TranscribeCppNative.ParakeetStreamExt>(
            nameof(TranscribeCppNative.ParakeetStreamExt.att_context_right)));

    [Fact]
    public void StreamUpdate_field_offsets_match_native_layout()
    {
        Assert.Equal(8, (int)Marshal.OffsetOf<TranscribeCppNative.StreamUpdate>("result_changed"));
        Assert.Equal(9, (int)Marshal.OffsetOf<TranscribeCppNative.StreamUpdate>("is_final"));
        Assert.Equal(12, (int)Marshal.OffsetOf<TranscribeCppNative.StreamUpdate>("revision"));
        Assert.Equal(16, (int)Marshal.OffsetOf<TranscribeCppNative.StreamUpdate>("input_received_ms"));
        Assert.Equal(40, (int)Marshal.OffsetOf<TranscribeCppNative.StreamUpdate>("committed_changed"));
    }
}
```

- [ ] **Step 2: Write the failing contract tests**

```csharp
// tests/Winpepper.Asr.Tests/TranscribeCpp/TranscribeCppContractTests.cs
using Winpepper.Asr.TranscribeCpp;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp;

public class TranscribeCppContractTests
{
    private const string GoodJson =
        """{"version":"0.1.3","header_hash":"86b16dd97ad1cb58","backends":["vulkan","cpu"],"lane":"cpu-vulkan"}""";

    [Fact]
    public void Parses_the_real_v013_contract_and_is_compatible()
    {
        var c = TranscribeCppContract.Parse(GoodJson);
        Assert.Equal("0.1.3", c.Version);
        Assert.Equal("86b16dd97ad1cb58", c.HeaderHash);
        Assert.True(c.IsCompatible);
    }

    [Fact]
    public void Wrong_version_is_incompatible()
        => Assert.False(TranscribeCppContract.Parse(
            """{"version":"0.2.0","header_hash":"86b16dd97ad1cb58"}""").IsCompatible);

    [Fact]
    public void Wrong_header_hash_is_incompatible()
        => Assert.False(TranscribeCppContract.Parse(
            """{"version":"0.1.3","header_hash":"deadbeefdeadbeef"}""").IsCompatible);

    [Fact]
    public void Missing_fields_throw_a_clear_error()
        => Assert.Throws<TranscribeCppException>(() => TranscribeCppContract.Parse("{}"));

    [Fact]
    public void Garbage_json_throws_a_clear_error()
        => Assert.Throws<TranscribeCppException>(() => TranscribeCppContract.Parse("not json"));
}
```

Note: `TranscribeCppException` is declared in this task (it's needed by
`Parse`); Task 4 reuses it.

- [ ] **Step 3: Run tests to verify they fail**

Run: `cd /home/dan/code/winpepper/.worktrees/nemotron-streaming && /home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q && /home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -class "*TranscribeCpp*" -notrait "Platform=Windows" 2>&1 | tail -20` (never `dotnet test` — VSTest is unreliable per AGENTS.md; all test runs in this plan use build + `dotnet exec`)
Expected: compile FAILURE — `TranscribeCppNative`/`TranscribeCppContract` do not exist.

- [ ] **Step 4: Write `TranscribeCppNative.cs`**

Port the spike's `Native` class verbatim, with ONE deliberate change: every
marshaled struct uses `[StructLayout(LayoutKind.Explicit)]` with the verified
offsets (the spec mandates Explicit; the spike proved the offsets).

```csharp
// src/Winpepper.Asr/TranscribeCpp/TranscribeCppNative.cs
using System.Runtime.InteropServices;

namespace Winpepper.Asr.TranscribeCpp;

/// <summary>
/// Raw P/Invoke surface for transcribe.cpp v0.1.3. ABI facts (struct sizes,
/// offsets, marshaling rules) were proven at runtime by the spike at
/// artifacts/transcribe-spike-src/Program.cs against contract.json
/// header_hash 86b16dd97ad1cb58. Every rule here is load-bearing:
/// - Cdecl everywhere; UTF-8 string INPUTS via LPUTF8Str; all const char*
///   RETURNS as IntPtr + PtrToStringUTF8 (never a marshaled string return —
///   the CLR would try to free library-owned storage).
/// - C bool returns => [return: MarshalAs(UnmanagedType.I1)]; C bool struct
///   fields => byte (bool would marshal as 4-byte BOOL and shift offsets).
/// - size_t => UIntPtr. Optional struct pointers => IntPtr (Zero = defaults).
/// </summary>
internal static class TranscribeCppNative
{
    private const string Dll = "transcribe";

    public const int BACKEND_AUTO = 0, BACKEND_CPU = 1, BACKEND_VULKAN = 3;
    public const int EXT_SLOT_STREAM = 1;
    public const uint EXT_KIND_PARAKEET_STREAM = 0x54534B50; // 'PKST'

    // transcribe_abi_struct ids
    public const int ABI_MODEL_LOAD_PARAMS = 0, ABI_STREAM_PARAMS = 3, ABI_CAPABILITIES = 4,
                     ABI_STREAM_UPDATE = 9, ABI_STREAM_TEXT = 10;

    // ---- structs (offsets runtime-verified by the spike; see plan table) ----

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct ModelLoadParams
    {
        [FieldOffset(0)] public ulong struct_size;
        [FieldOffset(8)] public int backend;      // transcribe_backend_request
        [FieldOffset(12)] public int gpu_device;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct StreamParams
    {
        [FieldOffset(0)] public ulong struct_size;
        [FieldOffset(8)] public IntPtr family;    // const struct transcribe_ext *
        [FieldOffset(16)] public int commit_policy;
        [FieldOffset(20)] public uint stable_prefix_agreement_n;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    public struct StreamUpdate
    {
        [FieldOffset(0)] public ulong struct_size;
        [FieldOffset(8)] public byte result_changed;
        [FieldOffset(9)] public byte is_final;
        [FieldOffset(12)] public int revision;
        [FieldOffset(16)] public long input_received_ms;
        [FieldOffset(24)] public long audio_committed_ms;
        [FieldOffset(32)] public long buffered_ms;
        [FieldOffset(40)] public byte committed_changed;
        [FieldOffset(41)] public byte tentative_changed;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct StreamText
    {
        [FieldOffset(0)] public ulong struct_size;
        [FieldOffset(8)] public IntPtr full_text;
        [FieldOffset(16)] public ulong full_text_bytes;
        [FieldOffset(24)] public IntPtr committed_text;
        [FieldOffset(32)] public ulong committed_text_bytes;
        [FieldOffset(40)] public IntPtr tentative_text;
        [FieldOffset(48)] public ulong tentative_text_bytes;
        [FieldOffset(56)] public ulong raw_tentative_start_bytes;
    }

    [StructLayout(LayoutKind.Explicit, Size = 56)]
    public struct Capabilities
    {
        [FieldOffset(0)] public ulong struct_size;
        [FieldOffset(8)] public int native_sample_rate;
        [FieldOffset(12)] public int n_languages;
        [FieldOffset(16)] public IntPtr languages;
        [FieldOffset(24)] public int max_timestamp_kind;
        [FieldOffset(28)] public byte supports_language_detect;
        [FieldOffset(29)] public byte supports_translate;
        [FieldOffset(30)] public byte supports_streaming;
        [FieldOffset(31)] public byte supports_spec_decode;
        [FieldOffset(32)] public long max_audio_ms;
        [FieldOffset(40)] public int n_translate_target_languages;
        [FieldOffset(48)] public IntPtr translate_target_languages;
    }

    // struct transcribe_parakeet_stream_ext { transcribe_ext ext; i32 att_context_right; }
    // sizeof(transcribe_ext) == 16 ({u64,u32} tail-padded), so att_context_right
    // is at OFFSET 16 and total size is 24. No ABI id exists for family exts —
    // this layout is asserted by tests + the Size attribute only.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct ParakeetStreamExt
    {
        [FieldOffset(0)] public ulong ext_size;
        [FieldOffset(8)] public uint ext_kind;
        [FieldOffset(16)] public int att_context_right;
    }

    // ---- logging ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void LogCallback(int level, IntPtr msg, IntPtr userdata);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void transcribe_log_set(LogCallback cb, IntPtr userdata);

    // ---- version / abi ----
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr transcribe_version();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr transcribe_version_commit();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern UIntPtr transcribe_abi_struct_size(int which);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr transcribe_status_string(int status);

    // ---- backends ----
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int transcribe_init_backends([MarshalAs(UnmanagedType.LPUTF8Str)] string dir);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool transcribe_backend_available(int kind);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int transcribe_backend_device_count();

    // ---- model / session ----
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void transcribe_model_load_params_init(ref ModelLoadParams p);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int transcribe_model_load_file(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref ModelLoadParams p, out IntPtr model);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void transcribe_model_free(IntPtr model);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void transcribe_capabilities_init(ref Capabilities c);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int transcribe_model_get_capabilities(IntPtr model, ref Capabilities c);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool transcribe_model_accepts_ext_kind(IntPtr model, int slot, uint kind);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int transcribe_session_init(IntPtr model, IntPtr sessionParams, out IntPtr session);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void transcribe_session_free(IntPtr session);

    // ---- batch (parity bench; verify signatures against /tmp/t013/transcribe.h) ----
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int transcribe_run(IntPtr session, float[] pcm, int nSamples, IntPtr runParams);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr transcribe_full_text(IntPtr session);

    // ---- streaming ----
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void transcribe_stream_params_init(ref StreamParams p);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void transcribe_parakeet_stream_ext_init(ref ParakeetStreamExt e);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int transcribe_stream_begin(IntPtr session, IntPtr runParams, IntPtr streamParams);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void transcribe_stream_update_init(ref StreamUpdate u);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int transcribe_stream_feed(IntPtr session, float[] pcm, int nSamples, ref StreamUpdate u);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int transcribe_stream_finalize(IntPtr session, ref StreamUpdate u);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void transcribe_stream_text_init(ref StreamText t);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int transcribe_stream_get_text(IntPtr session, ref StreamText t);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool transcribe_was_truncated(IntPtr session);

    public static string Status(int st)
        => Marshal.PtrToStringUTF8(transcribe_status_string(st)) ?? $"status {st}";
    public static string Str(IntPtr p)
        => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";
}
```

Before finishing this step, confirm the two batch signatures against the real
header: `grep -n "transcribe_run\b\|transcribe_full_text" /tmp/t013/transcribe.h`
— expected: `transcribe_status transcribe_run(struct transcribe_session*, const float*, int, const struct transcribe_run_params*)`
and `const char * transcribe_full_text(const struct transcribe_session *)`.
If they differ, match the header (the header is ABI truth) and note it in the
commit message.

- [ ] **Step 5: Write `TranscribeCppContract.cs`**

```csharp
// src/Winpepper.Asr/TranscribeCpp/TranscribeCppContract.cs
using System.Text.Json;

namespace Winpepper.Asr.TranscribeCpp;

/// <summary>Thrown for any transcribe.cpp binding failure (contract mismatch,
/// ABI mismatch, native error status). Callers treat it as "streaming engine
/// unavailable" and fall back to batch.</summary>
public sealed class TranscribeCppException : Exception
{
    public TranscribeCppException(string message) : base(message) { }
    public TranscribeCppException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// contract.json shipped inside the native runtime tarball. Validated BEFORE
/// any native library load: we only ever LoadLibrary a runtime whose contract
/// pins the exact version + header hash this binding was written against.
/// </summary>
public sealed record TranscribeCppContract(string Version, string HeaderHash)
{
    public const string RequiredVersion = "0.1.3";
    public const string RequiredHeaderHash = "86b16dd97ad1cb58";

    public bool IsCompatible => Version == RequiredVersion && HeaderHash == RequiredHeaderHash;

    public static TranscribeCppContract Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("version", out var v) ||
                !root.TryGetProperty("header_hash", out var h) ||
                v.ValueKind != JsonValueKind.String || h.ValueKind != JsonValueKind.String)
            {
                throw new TranscribeCppException(
                    "contract.json is missing required string fields 'version'/'header_hash'");
            }
            return new TranscribeCppContract(v.GetString()!, h.GetString()!);
        }
        catch (JsonException e)
        {
            throw new TranscribeCppException("contract.json is not valid JSON", e);
        }
    }

    public static TranscribeCppContract Load(string path) => Parse(File.ReadAllText(path));
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `cd /home/dan/code/winpepper/.worktrees/nemotron-streaming && /home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q && /home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -class "*TranscribeCpp*" -notrait "Platform=Windows" 2>&1 | tail -5`
Expected: PASS (13 tests).

- [ ] **Step 7: Full Linux suite + commit**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-streaming
./scripts/linux-tests.sh   # expect final line: LINUX SUITE: GREEN
git add src/Winpepper.Asr/TranscribeCpp tests/Winpepper.Asr.Tests/TranscribeCpp
git commit -m "feat(asr): transcribe.cpp v0.1.3 P/Invoke surface with pinned ABI layouts and contract validation

Explicit struct layouts locked to the spike-verified offsets
(model_load_params=16, stream_params=24, capabilities=56, stream_update=48,
stream_text=64, parakeet_stream_ext=24 with att_context_right at offset 16).
contract.json gate pins version 0.1.3 + header_hash 86b16dd97ad1cb58.

Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 2: Tar.gz extraction in the model downloader

**Files:**
- Create: `src/Winpepper.Models/TarGzExtractor.cs`
- Modify: `src/Winpepper.Models/ModelFile.cs` (add one optional property)
- Modify: `src/Winpepper.Models/ModelDownloader.cs` (post-verify extraction hook)
- Test: `tests/Winpepper.Models.Tests/TarGzExtractorTests.cs`
- Test: `tests/Winpepper.Models.Tests/ModelDownloaderExtractionTests.cs`

**Interfaces:**
- Consumes: `ModelDownloader.DownloadOneAsync` internals (`ModelDownloader.cs:60-175`): already-installed short-circuit at `:66-78`, final `File.Move(partialPath, finalPath, overwrite: true)` at `:173`. `ModelFile` record (`ModelFile.cs`).
- Produces: `ModelFile.ExtractToRelative : string?` (null = no extraction); `public static class TarGzExtractor { static void EnsureExtracted(string archivePath, string destinationDir, string archiveSha256); }`. Marker file convention: `<archivePath>.extracted` containing the archive's SHA-256 (lowercase hex); presence with matching content = already extracted.

- [ ] **Step 1: Write the failing extractor tests**

```csharp
// tests/Winpepper.Models.Tests/TarGzExtractorTests.cs
using System.Formats.Tar;
using System.IO.Compression;
using Winpepper.Models;
using Xunit;

namespace Winpepper.Models.Tests;

public class TarGzExtractorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wp-targz-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string MakeArchive(string name = "a.tar.gz")
    {
        // Mimic the real runtime tarball shape: one top-level directory.
        var src = Path.Combine(_dir, "src", "toplevel");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "contract.json"), """{"version":"0.1.3"}""");
        File.WriteAllText(Path.Combine(src, "transcribe.dll"), "fake dll bytes");
        var archive = Path.Combine(_dir, name);
        using var fs = File.Create(archive);
        using var gz = new GZipStream(fs, CompressionMode.Compress);
        TarFile.CreateFromDirectory(Path.Combine(_dir, "src"), gz, includeBaseDirectory: false);
        return archive;
    }

    [Fact]
    public void Extracts_archive_contents_and_writes_marker()
    {
        var archive = MakeArchive();
        var dest = Path.Combine(_dir, "runtime");
        TarGzExtractor.EnsureExtracted(archive, dest, "abc123");
        Assert.True(File.Exists(Path.Combine(dest, "toplevel", "contract.json")));
        Assert.True(File.Exists(Path.Combine(dest, "toplevel", "transcribe.dll")));
        Assert.Equal("abc123", File.ReadAllText(archive + ".extracted").Trim());
    }

    [Fact]
    public void Second_call_with_same_hash_is_a_no_op()
    {
        var archive = MakeArchive();
        var dest = Path.Combine(_dir, "runtime");
        TarGzExtractor.EnsureExtracted(archive, dest, "abc123");
        var sentinel = Path.Combine(dest, "toplevel", "extra.txt");
        File.WriteAllText(sentinel, "kept");           // would be wiped by a re-extract
        TarGzExtractor.EnsureExtracted(archive, dest, "abc123");
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public void Changed_hash_forces_a_clean_re_extract()
    {
        var archive = MakeArchive();
        var dest = Path.Combine(_dir, "runtime");
        TarGzExtractor.EnsureExtracted(archive, dest, "abc123");
        File.WriteAllText(Path.Combine(dest, "toplevel", "stale.txt"), "old");
        TarGzExtractor.EnsureExtracted(archive, dest, "def456");
        Assert.False(File.Exists(Path.Combine(dest, "toplevel", "stale.txt")));
        Assert.Equal("def456", File.ReadAllText(archive + ".extracted").Trim());
    }

    [Fact]
    public void Missing_marker_with_existing_dest_re_extracts()
    {
        var archive = MakeArchive();
        var dest = Path.Combine(_dir, "runtime");
        TarGzExtractor.EnsureExtracted(archive, dest, "abc123");
        File.Delete(archive + ".extracted");
        File.Delete(Path.Combine(dest, "toplevel", "transcribe.dll"));
        TarGzExtractor.EnsureExtracted(archive, dest, "abc123");
        Assert.True(File.Exists(Path.Combine(dest, "toplevel", "transcribe.dll")));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q && /home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Models.Tests/bin/Release/net9.0/Winpepper.Models.Tests.dll -class "*TarGzExtractor*" -notrait "Platform=Windows" 2>&1 | tail -5`
Expected: compile FAILURE — `TarGzExtractor` does not exist.

- [ ] **Step 3: Implement `TarGzExtractor`**

```csharp
// src/Winpepper.Models/TarGzExtractor.cs
using System.Formats.Tar;
using System.IO.Compression;

namespace Winpepper.Models;

/// <summary>
/// Idempotent .tar.gz extraction for model-bundle archives (the transcribe.cpp
/// native runtime). A marker file "&lt;archive&gt;.extracted" containing the
/// archive's SHA-256 records a completed extraction; a missing or stale marker
/// triggers a clean re-extract (destination dir is deleted first, so a
/// half-extracted tree can never be mistaken for a good one).
/// ORDERING IS LOAD-BEARING: the destination tree is deleted BEFORE the marker,
/// so a failed delete (Windows locks a loaded transcribe.dll and its tree)
/// leaves the old marker + old tree consistent instead of latching a sticky
/// "no marker, undeletable dir" state. A locked tree surfaces as a clear
/// restart-required error; the engine holder caches a loaded engine for the
/// process lifetime anyway, so an in-process runtime swap could never take
/// effect — restart-required is the honest contract.
/// TarFile.ExtractToDirectory rejects path-traversal entries by design.
/// </summary>
public static class TarGzExtractor
{
    public static void EnsureExtracted(string archivePath, string destinationDir, string archiveSha256)
    {
        var marker = archivePath + ".extracted";
        if (File.Exists(marker) && Directory.Exists(destinationDir) &&
            string.Equals(File.ReadAllText(marker).Trim(), archiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Directory.Exists(destinationDir))
        {
            try
            {
                Directory.Delete(destinationDir, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Loaded native DLLs lock the tree (engine already running).
                throw new IOException(
                    $"Cannot replace the extracted runtime at '{destinationDir}': files are in " +
                    "use (the streaming engine is loaded in this or another process). " +
                    "Restart the app, then retry the install.", e);
            }
        }
        if (File.Exists(marker)) File.Delete(marker);   // only after the tree is gone
        Directory.CreateDirectory(destinationDir);

        using (var fs = File.OpenRead(archivePath))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        {
            TarFile.ExtractToDirectory(gz, destinationDir, overwriteFiles: true);
        }

        File.WriteAllText(marker, archiveSha256);
    }
}
```

- [ ] **Step 4: Run extractor tests to verify they pass**

Run: `/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q && /home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Models.Tests/bin/Release/net9.0/Winpepper.Models.Tests.dll -class "*TarGzExtractor*" -notrait "Platform=Windows" 2>&1 | tail -5`
Expected: PASS (4 tests).

- [ ] **Step 5: Write the failing downloader-hook test**

Follow the existing `ModelDownloader` test style in
`tests/Winpepper.Models.Tests` (there are existing downloader tests using a
fake `IHttpRangeClient` — read one first and reuse its fake). The new test:

```csharp
// tests/Winpepper.Models.Tests/ModelDownloaderExtractionTests.cs
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Winpepper.Models;
using Xunit;

namespace Winpepper.Models.Tests;

public class ModelDownloaderExtractionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wp-dl-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static (byte[] Bytes, string Sha256) MakeArchiveBytes()
    {
        var tmp = Directory.CreateTempSubdirectory("wp-arc-").FullName;
        try
        {
            var top = Path.Combine(tmp, "toplevel");
            Directory.CreateDirectory(top);
            File.WriteAllText(Path.Combine(top, "contract.json"), "{}");
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
                TarFile.CreateFromDirectory(tmp, gz, includeBaseDirectory: false);
            var bytes = ms.ToArray();
            return (bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task Downloaded_archive_with_ExtractToRelative_is_extracted()
    {
        var (bytes, sha) = MakeArchiveBytes();
        var descriptor = new ModelDescriptor
        {
            Name = "test-runtime", Kind = ModelKind.Asr, DisplayName = "t",
            InstallDirRelative = "test-runtime",
            Files = new[]
            {
                new ModelFile
                {
                    RelativePath = "native.tar.gz",
                    Url = "https://example.invalid/native.tar.gz",
                    Sha256 = sha, SizeBytes = bytes.Length,
                    ExtractToRelative = "runtime",
                },
            },
        };
        // Use the SAME fake IHttpRangeClient the existing ModelDownloader tests
        // use (see the neighboring test file), serving `bytes` for the URL.
        var downloader = new ModelDownloader(new FakeRangeClient(bytes));
        await downloader.DownloadAsync(descriptor, _root,
            new Progress<DownloadProgress>(), CancellationToken.None);

        var modelDir = Path.Combine(_root, "test-runtime");
        Assert.True(File.Exists(Path.Combine(modelDir, "native.tar.gz")));           // archive kept
        Assert.True(File.Exists(Path.Combine(modelDir, "runtime", "toplevel", "contract.json")));
        Assert.Equal(sha, File.ReadAllText(Path.Combine(modelDir, "native.tar.gz.extracted")).Trim());
    }

    [Fact]
    public async Task Already_installed_archive_with_missing_extraction_is_healed()
    {
        var (bytes, sha) = MakeArchiveBytes();
        var descriptor = /* same descriptor as above */ BuildDescriptor(bytes, sha);
        var modelDir = Path.Combine(_root, "test-runtime");
        Directory.CreateDirectory(modelDir);
        await File.WriteAllBytesAsync(Path.Combine(modelDir, "native.tar.gz"), bytes); // pre-installed, never extracted

        var downloader = new ModelDownloader(new FakeRangeClient(bytes));
        await downloader.DownloadAsync(descriptor, _root,
            new Progress<DownloadProgress>(), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(modelDir, "runtime", "toplevel", "contract.json")));
    }
}
```

(`FakeRangeClient` / `BuildDescriptor`: copy the fake-client shape from the
existing downloader tests in the same directory; `BuildDescriptor` is the
descriptor literal from the first test extracted to a helper. Do not invent a
new fake style.)

- [ ] **Step 6: Run to verify failure**

Run: `/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q && /home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Models.Tests/bin/Release/net9.0/Winpepper.Models.Tests.dll -class "*ModelDownloaderExtraction*" -notrait "Platform=Windows" 2>&1 | tail -5`
Expected: compile FAILURE — `ModelFile.ExtractToRelative` does not exist.

- [ ] **Step 7: Implement the property + hook**

`src/Winpepper.Models/ModelFile.cs` — append inside the record:

```csharp
    /// <summary>
    /// Optional: after download + SHA-256 verification, extract this .tar.gz
    /// archive into this directory (relative to the model install dir). The
    /// archive file itself is KEPT so IsFullyInstalled and hash re-verification
    /// keep working. Extraction is idempotent (TarGzExtractor marker file).
    /// </summary>
    public string? ExtractToRelative { get; init; }
```

`src/Winpepper.Models/ModelDownloader.cs` — two edits in `DownloadOneAsync`:

1. In the already-installed short-circuit (`:66-78`), just before the
   `Report(... Complete)` + `return`, add:
   ```csharp
   EnsureExtracted(modelDir, file);
   ```
2. After the final `File.Move(partialPath, finalPath, overwrite: true);`, add:
   ```csharp
   EnsureExtracted(modelDir, file);
   ```
3. Add the private helper to the class:
   ```csharp
   private static void EnsureExtracted(string modelDir, ModelFile file)
   {
       if (file.ExtractToRelative is null) return;
       TarGzExtractor.EnsureExtracted(
           Path.Combine(modelDir, file.RelativePath),
           Path.Combine(modelDir, file.ExtractToRelative),
           file.Sha256);
   }
   ```

- [ ] **Step 8: Run tests, full suite, commit**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q && /home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Models.Tests/bin/Release/net9.0/Winpepper.Models.Tests.dll -notrait "Platform=Windows" 2>&1 | tail -5   # PASS
./scripts/linux-tests.sh    # LINUX SUITE: GREEN
git add src/Winpepper.Models tests/Winpepper.Models.Tests
git commit -m "feat(models): idempotent tar.gz extraction for archive model files

Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 3: Registry — `ModelKind.StreamingAsr` + the nemotron descriptor (pinned hashes)

**Files:**
- Modify: `src/Winpepper.Models/ModelKind.cs`
- Modify: `src/Winpepper.Models/ModelRegistry.cs`
- Modify: `scripts/verify-model-hashes.ps1` (URL list at `:9-17` — add both new URLs)
- Test: `tests/Winpepper.Models.Tests/ModelRegistryCatalogTests.cs` (extend)

**Interfaces:**
- Consumes: `ModelDescriptor`/`ModelFile` (incl. Task 2's `ExtractToRelative`).
- Produces: `ModelKind.StreamingAsr`; `ModelRegistry.StreamingAsrName = "nemotron-streaming-en"`; the descriptor with `InstallDirRelative = "nemotron-streaming-en"`, GGUF file `nemotron-speech-streaming-en-0.6b-Q8_0.gguf`, archive file `transcribe-native-0.1.3-windows-x86_64-cpu-vulkan.tar.gz` with `ExtractToRelative = "runtime"`. On-disk layout consumed by Task 4's locator:
  `models\nemotron-streaming-en\nemotron-speech-streaming-en-0.6b-Q8_0.gguf` and
  `models\nemotron-streaming-en\runtime\transcribe-native-windows-x86_64-cpu-vulkan\{transcribe.dll, ggml*.dll, contract.json}`.

- [ ] **Step 1: Write the failing catalog tests**

Read `tests/Winpepper.Models.Tests/ModelRegistryCatalogTests.cs` first and add
in its style:

```csharp
    [Fact]
    public void Registry_contains_the_nemotron_streaming_model()
    {
        var d = new ModelRegistry().Find(ModelRegistry.StreamingAsrName);
        Assert.NotNull(d);
        Assert.Equal(ModelKind.StreamingAsr, d!.Kind);
        Assert.Equal("nemotron-streaming-en", d.InstallDirRelative);
        Assert.Equal(2, d.Files.Count);

        var gguf = d.Files.Single(f => f.RelativePath.EndsWith(".gguf"));
        Assert.Equal("nemotron-speech-streaming-en-0.6b-Q8_0.gguf", gguf.RelativePath);
        Assert.Equal(729_650_176, gguf.SizeBytes);
        Assert.Equal("90d8c89714cd31efc88be62a40c6b2bea57e0cc2063af1ffe2c28f1a228ca110", gguf.Sha256);
        Assert.Null(gguf.ExtractToRelative);

        var runtime = d.Files.Single(f => f.RelativePath.EndsWith(".tar.gz"));
        Assert.Equal(25_957_910, runtime.SizeBytes);
        Assert.Equal("9f536cb0fb839bd305e6d92fb214fd417c7718a416a6c7646a9911fbd56fdad5", runtime.Sha256);
        Assert.Equal("runtime", runtime.ExtractToRelative);
        Assert.StartsWith("https://github.com/handy-computer/transcribe.cpp/releases/download/v0.1.3/", runtime.Url);
    }

    [Fact]
    public void StreamingAsr_kind_never_appears_in_the_batch_asr_list()
        => Assert.DoesNotContain(new ModelRegistry().ByKind(ModelKind.Asr),
            d => d.Kind == ModelKind.StreamingAsr);

    [Fact]
    public void ResolveOrDefault_throws_for_StreamingAsr_kind_defaults()
        // StreamingAsr deliberately has no default: it is opt-in-install only,
        // never a resolvable AsrModelName. This test documents that contract.
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new ModelRegistry().ResolveOrDefault(null, ModelKind.StreamingAsr));
```

- [ ] **Step 2: Run to verify failure**

Run: `/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q && /home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Models.Tests/bin/Release/net9.0/Winpepper.Models.Tests.dll -class "*ModelRegistryCatalog*" -notrait "Platform=Windows" 2>&1 | tail -5`
Expected: compile FAILURE (`StreamingAsr`, `StreamingAsrName` missing).

- [ ] **Step 3: Implement**

`ModelKind.cs`:
```csharp
public enum ModelKind
{
    Asr,
    Cleanup,
    /// <summary>Streaming-only ASR engine (transcribe.cpp GGUF + native runtime).
    /// Never selectable as AsrModelName; opt-in install, used only when
    /// StreamingEnabled and installed.</summary>
    StreamingAsr,
}
```

`ModelRegistry.cs` — add the const next to the existing ones:
```csharp
    public const string StreamingAsrName = "nemotron-streaming-en";
```
and append to the `_all` list:
```csharp
            new ModelDescriptor
            {
                Name = StreamingAsrName,
                Kind = ModelKind.StreamingAsr,
                DisplayName = "Nemotron Speech Streaming (0.6B, Q8_0 GGUF, English)",
                InstallDirRelative = "nemotron-streaming-en",
                Files = new[]
                {
                    new ModelFile
                    {
                        RelativePath = "nemotron-speech-streaming-en-0.6b-Q8_0.gguf",
                        Url = "https://huggingface.co/handy-computer/nemotron-speech-streaming-en-0.6b-gguf/resolve/main/nemotron-speech-streaming-en-0.6b-Q8_0.gguf",
                        Sha256 = "90d8c89714cd31efc88be62a40c6b2bea57e0cc2063af1ffe2c28f1a228ca110",
                        SizeBytes = 729_650_176,
                    },
                    new ModelFile
                    {
                        RelativePath = "transcribe-native-0.1.3-windows-x86_64-cpu-vulkan.tar.gz",
                        Url = "https://github.com/handy-computer/transcribe.cpp/releases/download/v0.1.3/transcribe-native-0.1.3-windows-x86_64-cpu-vulkan.tar.gz",
                        Sha256 = "9f536cb0fb839bd305e6d92fb214fd417c7718a416a6c7646a9911fbd56fdad5",
                        SizeBytes = 25_957_910,
                        ExtractToRelative = "runtime",
                    },
                },
            },
```

The hashes/sizes above are pinned facts (see "Pinned acquisition facts"): they
were verified against the HuggingFace LFS metadata (`lfs.oid sha256`), the
GitHub release asset `digest`, AND a local `sha256sum` of the spike's
downloaded copies — all three agree. Do not recompute; if any check in Step 5
disagrees, STOP and investigate (possible supply-chain drift).

`scripts/verify-model-hashes.ps1` — add both URLs to the hard-coded list at
`:9-17` (same string format as the existing entries).

- [ ] **Step 4: Check ViewModel/consumer fallout**

`grep -rn "ModelKind\." src/ tests/ scripts/ --include=*.cs` and fix any
exhaustive `switch` or `Kind == ModelKind.Asr ? ... : ...` two-way branch that
now mis-routes `StreamingAsr` (known one: `ModelsTabViewModel.cs:67` routes
progress to `d.Kind == ModelKind.Asr ? AsrCard : CleanupCard` — change to a
`switch` that ignores/short-circuits `StreamingAsr` for now; Task 7 gives it a
real card). `ResolveOrDefault` stays as-is (throwing for StreamingAsr is the
documented contract).

- [ ] **Step 5: Verify the pinned hashes against a fresh download of the small artifact**

The tarball is only 26 MB — fresh-download it once to prove the URL serves the
pinned bytes (the GGUF's 730 MB re-download is skipped; its hash is already
triple-confirmed including HF's own LFS metadata):

```bash
curl -sL -o /tmp/nemotron-runtime-check.tar.gz \
  https://github.com/handy-computer/transcribe.cpp/releases/download/v0.1.3/transcribe-native-0.1.3-windows-x86_64-cpu-vulkan.tar.gz \
  && sha256sum /tmp/nemotron-runtime-check.tar.gz
```
Expected output hash: `9f536cb0fb839bd305e6d92fb214fd417c7718a416a6c7646a9911fbd56fdad5`. Delete the temp file after.

- [ ] **Step 6: Run tests, full suite, commit**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q && /home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Models.Tests/bin/Release/net9.0/Winpepper.Models.Tests.dll -notrait "Platform=Windows" 2>&1 | tail -5   # PASS
./scripts/linux-tests.sh    # LINUX SUITE: GREEN
git add src/Winpepper.Models tests/Winpepper.Models.Tests scripts/verify-model-hashes.ps1
git commit -m "feat(models): register nemotron-streaming-en (Q8_0 GGUF + transcribe.cpp v0.1.3 runtime) with pinned hashes

Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 4: The engine — `ITranscribeCppEngine` + real `TranscribeCppEngine` + locator

**Files:**
- Create: `src/Winpepper.Asr/TranscribeCpp/ITranscribeCppEngine.cs`
- Create: `src/Winpepper.Asr/TranscribeCpp/TranscribeCppEngine.cs`
- Create: `src/Winpepper.Asr/TranscribeCpp/NemotronStreamingModel.cs`
- Test: `tests/Winpepper.Asr.Tests/TranscribeCpp/TranscribeCppEngineGateTests.cs`
- Test: `tests/Winpepper.IntegrationTests/NemotronLayoutContractTests.cs` (registry paths == locator paths)

**Interfaces:**
- Consumes: Task 1's `TranscribeCppNative`, `TranscribeCppContract`, `TranscribeCppException`; Task 3's on-disk layout.
- Produces (exact, used by Tasks 5/6/8):

```csharp
namespace Winpepper.Asr.TranscribeCpp;

public interface ITranscribeCppStream : IDisposable
{
    /// <summary>Feed 16 kHz mono float samples. Returns the latest committed
    /// text when it changed, else null. Throws TranscribeCppException on any
    /// native error. Single-threaded.</summary>
    string? Feed(float[] samples, int count);

    /// <summary>Flush + finalize. Returns the final transcript (full_text) and
    /// the was_truncated flag. May be called with zero prior feeds.</summary>
    (string Text, bool WasTruncated) Finalize();
}

public interface ITranscribeCppEngine : IDisposable
{
    string ModelName { get; }
    /// <summary>Begin one streaming session (one per dictation). Acquires the
    /// engine-wide compute gate for the STREAM'S LIFETIME (released when the
    /// stream is disposed) — transcribe.cpp 0.x allows at most one compute in
    /// flight per model (see Global Constraints). Throws TranscribeCppException
    /// if the gate cannot be acquired within 5 s (previous dictation's stream
    /// not yet disposed) — callers fall back to batch.
    /// attContextRight in encoder frames: {13,6,1,0} = {1040,480,80,0} ms.</summary>
    ITranscribeCppStream BeginStream(int attContextRight);
    /// <summary>Offline single-utterance transcription on a dedicated native
    /// session (bench parity reference; not used by the app pipeline). Holds
    /// the same compute gate for the duration of the call.</summary>
    string TranscribeBatch(float[] mono16k);
}
```

and `NemotronStreamingModel` (static): `Name = "nemotron-streaming-en"`,
`GgufFileName = "nemotron-speech-streaming-en-0.6b-Q8_0.gguf"`,
`RuntimeDirRelative = Path.Combine("nemotron-streaming-en", "runtime", "transcribe-native-windows-x86_64-cpu-vulkan")`,
`ModelFileRelative = Path.Combine("nemotron-streaming-en", GgufFileName)`,
`string GgufPath(string modelsRoot)`, `string RuntimeDir(string modelsRoot)`,
`bool IsInstalled(string modelsRoot)` (gguf + transcribe.dll + contract.json all exist);
and `TranscribeCppEngine` with `static TranscribeCppEngine Load(string runtimeDir, string modelPath, Action<string>? logWarning = null)`.

- [ ] **Step 1: Write the failing gate tests (Linux-runnable — they exercise the pre-native gates)**

```csharp
// tests/Winpepper.Asr.Tests/TranscribeCpp/TranscribeCppEngineGateTests.cs
using Winpepper.Asr.TranscribeCpp;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp;

public class TranscribeCppEngineGateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wp-eng-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Load_fails_loud_when_contract_json_is_missing()
    {
        var ex = Assert.Throws<TranscribeCppException>(
            () => TranscribeCppEngine.Load(_dir, Path.Combine(_dir, "m.gguf")));
        Assert.Contains("contract.json", ex.Message);
    }

    [Fact]
    public void Load_fails_loud_on_contract_mismatch_before_touching_the_native_library()
    {
        File.WriteAllText(Path.Combine(_dir, "contract.json"),
            """{"version":"9.9.9","header_hash":"0000000000000000"}""");
        var ex = Assert.Throws<TranscribeCppException>(
            () => TranscribeCppEngine.Load(_dir, Path.Combine(_dir, "m.gguf")));
        Assert.Contains("9.9.9", ex.Message);   // message names the found version
        Assert.Contains("0.1.3", ex.Message);   // and the required one
    }

    [Fact]
    public void NemotronStreamingModel_IsInstalled_requires_all_three_files()
    {
        Assert.False(NemotronStreamingModel.IsInstalled(_dir));
        var modelDir = Path.Combine(_dir, "nemotron-streaming-en");
        var runtime = Path.Combine(modelDir, "runtime", "transcribe-native-windows-x86_64-cpu-vulkan");
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(modelDir, NemotronStreamingModel.GgufFileName), "x");
        Assert.False(NemotronStreamingModel.IsInstalled(_dir));
        File.WriteAllText(Path.Combine(runtime, "transcribe.dll"), "x");
        Assert.False(NemotronStreamingModel.IsInstalled(_dir));
        File.WriteAllText(Path.Combine(runtime, "contract.json"), "{}");
        Assert.True(NemotronStreamingModel.IsInstalled(_dir));
    }
}
```

And the cross-project layout contract (in `Winpepper.IntegrationTests`, which
may reference both `Winpepper.Models` and `Winpepper.Asr` — add the project
references to its csproj if absent):

```csharp
// tests/Winpepper.IntegrationTests/NemotronLayoutContractTests.cs
using Winpepper.Asr.TranscribeCpp;
using Winpepper.Models;
using Xunit;

namespace Winpepper.IntegrationTests;

public class NemotronLayoutContractTests
{
    // The registry (download side) and the locator (load side) must agree on
    // the on-disk layout, or install would succeed and the engine still miss it.
    [Fact]
    public void Registry_descriptor_and_locator_agree_on_paths()
    {
        var d = new ModelRegistry().Find(ModelRegistry.StreamingAsrName)!;
        Assert.Equal(NemotronStreamingModel.Name, d.Name);
        var gguf = d.Files.Single(f => f.RelativePath.EndsWith(".gguf"));
        Assert.Equal(
            Path.Combine(d.InstallDirRelative, gguf.RelativePath),
            NemotronStreamingModel.ModelFileRelative);
        var archive = d.Files.Single(f => f.ExtractToRelative is not null);
        // Locator's runtime dir = model dir + ExtractToRelative + the tarball's top-level dir
        Assert.StartsWith(
            Path.Combine(d.InstallDirRelative, archive.ExtractToRelative!),
            NemotronStreamingModel.RuntimeDirRelative);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q && /home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -class "*TranscribeCppEngineGate*" -notrait "Platform=Windows" 2>&1 | tail -5`
Expected: compile FAILURE.

- [ ] **Step 3: Implement `NemotronStreamingModel` + `ITranscribeCppEngine.cs`**

```csharp
// src/Winpepper.Asr/TranscribeCpp/NemotronStreamingModel.cs
namespace Winpepper.Asr.TranscribeCpp;

/// <summary>On-disk layout of the installed nemotron streaming model. Must
/// stay in lockstep with the ModelRegistry descriptor (enforced by
/// NemotronLayoutContractTests).</summary>
public static class NemotronStreamingModel
{
    public const string Name = "nemotron-streaming-en";
    public const string GgufFileName = "nemotron-speech-streaming-en-0.6b-Q8_0.gguf";
    /// <summary>The tarball extracts with ONE top-level directory.</summary>
    public const string TarballTopLevelDir = "transcribe-native-windows-x86_64-cpu-vulkan";

    public static string ModelFileRelative => Path.Combine(Name, GgufFileName);
    public static string RuntimeDirRelative => Path.Combine(Name, "runtime", TarballTopLevelDir);

    public static string GgufPath(string modelsRoot) => Path.Combine(modelsRoot, ModelFileRelative);
    public static string RuntimeDir(string modelsRoot) => Path.Combine(modelsRoot, RuntimeDirRelative);

    public static bool IsInstalled(string modelsRoot)
        => File.Exists(GgufPath(modelsRoot))
        && File.Exists(Path.Combine(RuntimeDir(modelsRoot), "transcribe.dll"))
        && File.Exists(Path.Combine(RuntimeDir(modelsRoot), "contract.json"));
}
```

`ITranscribeCppEngine.cs`: exactly the two interfaces from the Interfaces block above.

- [ ] **Step 4: Implement `TranscribeCppEngine`**

Init order is the spike's, with contract.json FIRST (before any native call)
and a DLL resolver so the library loads from the models dir, not the app dir:

```csharp
// src/Winpepper.Asr/TranscribeCpp/TranscribeCppEngine.cs
using System.Runtime.InteropServices;

namespace Winpepper.Asr.TranscribeCpp;

/// <summary>
/// Real transcribe.cpp engine. Load() performs, in order:
///  1. contract.json gate (exact version 0.1.3 + header_hash 86b16dd97ad1cb58)
///     — BEFORE any native library is loaded;
///  2. DllImportResolver registration mapping "transcribe" to
///     &lt;runtimeDir&gt;\transcribe.dll (process-wide, first runtimeDir wins);
///  3. log callback install (static delegate, process lifetime);
///  4. native version string gate (must equal 0.1.3);
///  5. ABI struct-size gate for every marshaled struct (non-short-circuit,
///     all mismatches reported);
///  6. transcribe_init_backends(runtimeDir) — REQUIRED before model load
///     (GGML_BACKEND_DL build; ggml-*.dll live in runtimeDir);
///  7. model load with backend = CPU (Vulkan measured unusable: ~16 s warm-up);
///  8. capabilities gate: supports_streaming, native_sample_rate == 16000,
///     PKST ext accepted.
/// Any failure throws TranscribeCppException — callers fall back to batch.
/// The model handle lives until Dispose (in practice: process lifetime via
/// NemotronEngineHolder). Each BeginStream/TranscribeBatch uses its own native
/// session. A stream/session is single-threaded, AND — v0.1.3 header contract
/// (transcribe.h:11-20) — at most ONE compute (run or active stream) may be in
/// flight across ALL sessions of a model: _computeGate serializes them.
/// BeginStream holds the gate for the stream's lifetime (released on stream
/// dispose); TranscribeBatch holds it per call. A 5 s acquire timeout turns a
/// stuck predecessor into a TranscribeCppException (=> batch fallback), never
/// a deadlock.
/// </summary>
public sealed class TranscribeCppEngine : ITranscribeCppEngine
{
    private static readonly object s_processInit = new();
    private static bool s_resolverInstalled;
    private static string? s_runtimeDir;
    private static Action<string>? s_logWarning;
    // Keep the delegate alive for process lifetime — native holds the pointer.
    private static readonly TranscribeCppNative.LogCallback s_log = (level, msg, _) =>
    {
        if (level == 2 /*WARN*/ || level == 3 /*ERROR*/)
            s_logWarning?.Invoke($"[transcribe.cpp:{(level == 3 ? "ERROR" : "WARN")}] {TranscribeCppNative.Str(msg)}");
    };

    private readonly IntPtr _model;
    // v0.1.3 contract: at most one compute in flight per model across ALL
    // sessions (transcribe.h:11-20). BeginStream holds this for the stream's
    // lifetime; TranscribeBatch per call.
    private readonly SemaphoreSlim _computeGate = new(1, 1);
    private static readonly TimeSpan s_gateTimeout = TimeSpan.FromSeconds(5);
    private bool _disposed;

    public string ModelName { get; }

    private TranscribeCppEngine(IntPtr model, string modelName)
    {
        _model = model;
        ModelName = modelName;
    }

    public static TranscribeCppEngine Load(string runtimeDir, string modelPath, Action<string>? logWarning = null)
    {
        // 1. contract gate — pure file IO, safe on any OS, BEFORE LoadLibrary.
        var contractPath = Path.Combine(runtimeDir, "contract.json");
        if (!File.Exists(contractPath))
            throw new TranscribeCppException($"contract.json not found in runtime dir: {runtimeDir}");
        var contract = TranscribeCppContract.Load(contractPath);
        if (!contract.IsCompatible)
            throw new TranscribeCppException(
                $"transcribe.cpp runtime contract mismatch: found version={contract.Version} " +
                $"header_hash={contract.HeaderHash}, require version={TranscribeCppContract.RequiredVersion} " +
                $"header_hash={TranscribeCppContract.RequiredHeaderHash}. Refusing to load.");

        if (!OperatingSystem.IsWindows())
            throw new TranscribeCppException("transcribe.cpp engine is Windows-only in winpepper");
        if (IntPtr.Size != 8)
            throw new TranscribeCppException("transcribe.cpp binding requires a 64-bit process");

        lock (s_processInit)
        {
            if (!s_resolverInstalled)
            {
                s_runtimeDir = runtimeDir;
                s_logWarning = logWarning;
                NativeLibrary.SetDllImportResolver(typeof(TranscribeCppEngine).Assembly,
                    (name, _, _) => name == "transcribe"
                        ? NativeLibrary.Load(Path.Combine(s_runtimeDir!, "transcribe.dll"))
                        : IntPtr.Zero);
                s_resolverInstalled = true;
                // 3. first native call, per header contract: once, at startup.
                // transcribe.dll statically imports the VC++ 2015-2022 x64 CRT
                // (msvcp140/vcruntime140/vcruntime140_1 — verified by PE import
                // dump); on a machine without the redist the first call throws
                // DllNotFoundException. Name the fix in the error.
                try
                {
                    TranscribeCppNative.transcribe_log_set(s_log, IntPtr.Zero);
                }
                catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
                {
                    throw new TranscribeCppException(
                        "failed to load transcribe.dll — likely missing the Microsoft " +
                        "Visual C++ 2015-2022 x64 Redistributable (msvcp140/vcruntime140). " +
                        "Install it from aka.ms/vs/17/release/vc_redist.x64.exe and retry.", e);
                }
            }
            else if (!string.Equals(s_runtimeDir, runtimeDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new TranscribeCppException(
                    $"transcribe.cpp already initialized from '{s_runtimeDir}'; cannot re-init from '{runtimeDir}' (restart required)");
            }

            // 4. version gate
            var ver = TranscribeCppNative.Str(TranscribeCppNative.transcribe_version());
            if (ver != TranscribeCppContract.RequiredVersion)
                throw new TranscribeCppException($"native transcribe version is {ver}, require {TranscribeCppContract.RequiredVersion}");

            // 5. ABI gate — every marshaled struct, all reported (& not &&).
            var mismatches = new List<string>();
            void Abi(int id, int managed, string name)
            {
                var native = TranscribeCppNative.transcribe_abi_struct_size(id).ToUInt64();
                if (native != (ulong)managed) mismatches.Add($"{name}: native={native} managed={managed}");
            }
            Abi(TranscribeCppNative.ABI_MODEL_LOAD_PARAMS, Marshal.SizeOf<TranscribeCppNative.ModelLoadParams>(), "model_load_params");
            Abi(TranscribeCppNative.ABI_STREAM_PARAMS, Marshal.SizeOf<TranscribeCppNative.StreamParams>(), "stream_params");
            Abi(TranscribeCppNative.ABI_CAPABILITIES, Marshal.SizeOf<TranscribeCppNative.Capabilities>(), "capabilities");
            Abi(TranscribeCppNative.ABI_STREAM_UPDATE, Marshal.SizeOf<TranscribeCppNative.StreamUpdate>(), "stream_update");
            Abi(TranscribeCppNative.ABI_STREAM_TEXT, Marshal.SizeOf<TranscribeCppNative.StreamText>(), "stream_text");
            if (mismatches.Count > 0)
                throw new TranscribeCppException("ABI struct size mismatch: " + string.Join("; ", mismatches));

            // 6. dynamic ggml backends live beside transcribe.dll
            var st = TranscribeCppNative.transcribe_init_backends(runtimeDir);
            if (st != 0)
                throw new TranscribeCppException($"transcribe_init_backends failed: {TranscribeCppNative.Status(st)}");
        }

        // 7. model load — CPU backend only (see plan: Vulkan warm-up unusable).
        var p = new TranscribeCppNative.ModelLoadParams();
        TranscribeCppNative.transcribe_model_load_params_init(ref p);
        p.backend = TranscribeCppNative.BACKEND_CPU;
        var stLoad = TranscribeCppNative.transcribe_model_load_file(modelPath, ref p, out var model);
        if (stLoad != 0)
            throw new TranscribeCppException($"model load failed ({modelPath}): {TranscribeCppNative.Status(stLoad)}");

        try
        {
            // 8. capability gates
            var caps = new TranscribeCppNative.Capabilities();
            TranscribeCppNative.transcribe_capabilities_init(ref caps);
            var stCaps = TranscribeCppNative.transcribe_model_get_capabilities(model, ref caps);
            if (stCaps != 0)
                throw new TranscribeCppException($"get_capabilities failed: {TranscribeCppNative.Status(stCaps)}");
            if (caps.supports_streaming == 0)
                throw new TranscribeCppException("model does not support streaming");
            if (caps.native_sample_rate != 16000)
                throw new TranscribeCppException($"model native_sample_rate is {caps.native_sample_rate}, require 16000");
            if (!TranscribeCppNative.transcribe_model_accepts_ext_kind(
                    model, TranscribeCppNative.EXT_SLOT_STREAM, TranscribeCppNative.EXT_KIND_PARAKEET_STREAM))
                throw new TranscribeCppException("model rejects the PKST stream extension");

            return new TranscribeCppEngine(model, NemotronStreamingModel.Name);
        }
        catch
        {
            TranscribeCppNative.transcribe_model_free(model);
            throw;
        }
    }

    public ITranscribeCppStream BeginStream(int attContextRight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // One compute in flight per model: hold the gate for the stream's
        // lifetime. A previous dictation's stream normally disposes in ms; a
        // 5 s timeout means a stuck one degrades to batch, never corrupts.
        if (!_computeGate.Wait(s_gateTimeout))
            throw new TranscribeCppException(
                "another transcription is still active on the engine (compute gate timeout)");
        try
        {
            return BeginStreamHoldingGate(attContextRight);
        }
        catch
        {
            _computeGate.Release();
            throw;
        }
    }

    private ITranscribeCppStream BeginStreamHoldingGate(int attContextRight)
    {
        var st = TranscribeCppNative.transcribe_session_init(_model, IntPtr.Zero, out var session);
        if (st != 0)
            throw new TranscribeCppException($"session_init failed: {TranscribeCppNative.Status(st)}");
        try
        {
            // PKST ext + stream params must be heap memory: stream_begin reads
            // raw pointers. begin copies everything out — free immediately after.
            var ext = new TranscribeCppNative.ParakeetStreamExt();
            TranscribeCppNative.transcribe_parakeet_stream_ext_init(ref ext);
            ext.att_context_right = attContextRight;
            var pExt = Marshal.AllocHGlobal(Marshal.SizeOf<TranscribeCppNative.ParakeetStreamExt>());
            var pSp = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(ext, pExt, false);
                var sp = new TranscribeCppNative.StreamParams();
                TranscribeCppNative.transcribe_stream_params_init(ref sp);
                sp.family = pExt;
                pSp = Marshal.AllocHGlobal(Marshal.SizeOf<TranscribeCppNative.StreamParams>());
                Marshal.StructureToPtr(sp, pSp, false);
                var stBegin = TranscribeCppNative.transcribe_stream_begin(session, IntPtr.Zero, pSp);
                if (stBegin != 0)
                    throw new TranscribeCppException($"stream_begin failed: {TranscribeCppNative.Status(stBegin)}");
            }
            finally
            {
                Marshal.FreeHGlobal(pExt);
                if (pSp != IntPtr.Zero) Marshal.FreeHGlobal(pSp);
            }
            return new NativeStream(session, () => _computeGate.Release());
        }
        catch
        {
            TranscribeCppNative.transcribe_session_free(session);
            throw;
        }
    }

    public string TranscribeBatch(float[] mono16k)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (mono16k.Length == 0) return "";
        if (!_computeGate.Wait(s_gateTimeout))
            throw new TranscribeCppException(
                "another transcription is still active on the engine (compute gate timeout)");
        try
        {
            var st = TranscribeCppNative.transcribe_session_init(_model, IntPtr.Zero, out var session);
            if (st != 0)
                throw new TranscribeCppException($"session_init failed: {TranscribeCppNative.Status(st)}");
            try
            {
                var stRun = TranscribeCppNative.transcribe_run(session, mono16k, mono16k.Length, IntPtr.Zero);
                if (stRun != 0)
                    throw new TranscribeCppException($"transcribe_run failed: {TranscribeCppNative.Status(stRun)}");
                // Copy immediately — pointer dies with the session.
                return TranscribeCppNative.Str(TranscribeCppNative.transcribe_full_text(session));
            }
            finally
            {
                TranscribeCppNative.transcribe_session_free(session);
            }
        }
        finally
        {
            _computeGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TranscribeCppNative.transcribe_model_free(_model);
    }

    private sealed class NativeStream : ITranscribeCppStream
    {
        private readonly IntPtr _session;
        private readonly Action _releaseComputeGate;
        private string _lastCommitted = "";
        private bool _disposed;

        public NativeStream(IntPtr session, Action releaseComputeGate)
        {
            _session = session;
            _releaseComputeGate = releaseComputeGate;
        }

        public string? Feed(float[] samples, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (count <= 0) return null;
            var chunk = samples;
            if (count != samples.Length)
            {
                chunk = new float[count];
                Array.Copy(samples, chunk, count);
            }
            var upd = new TranscribeCppNative.StreamUpdate();
            TranscribeCppNative.transcribe_stream_update_init(ref upd);
            var st = TranscribeCppNative.transcribe_stream_feed(_session, chunk, count, ref upd);
            if (st != 0)
                throw new TranscribeCppException($"stream_feed failed: {TranscribeCppNative.Status(st)}");
            if (upd.result_changed == 0) return null;

            // Copy strings IMMEDIATELY — pointers die on the next feed/finalize.
            var txt = new TranscribeCppNative.StreamText();
            TranscribeCppNative.transcribe_stream_text_init(ref txt);
            if (TranscribeCppNative.transcribe_stream_get_text(_session, ref txt) != 0) return null;
            var committed = TranscribeCppNative.Str(txt.committed_text);
            if (committed == _lastCommitted) return null;
            _lastCommitted = committed;
            return committed;
        }

        public (string Text, bool WasTruncated) Finalize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var upd = new TranscribeCppNative.StreamUpdate();
            TranscribeCppNative.transcribe_stream_update_init(ref upd);
            var st = TranscribeCppNative.transcribe_stream_finalize(_session, ref upd);
            if (st != 0)
                throw new TranscribeCppException($"stream_finalize failed: {TranscribeCppNative.Status(st)}");
            var txt = new TranscribeCppNative.StreamText();
            TranscribeCppNative.transcribe_stream_text_init(ref txt);
            var stTxt = TranscribeCppNative.transcribe_stream_get_text(_session, ref txt);
            if (stTxt != 0)
                throw new TranscribeCppException($"stream_get_text failed: {TranscribeCppNative.Status(stTxt)}");
            var full = TranscribeCppNative.Str(txt.full_text);
            return (full, TranscribeCppNative.transcribe_was_truncated(_session));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                TranscribeCppNative.transcribe_session_free(_session);
            }
            finally
            {
                _releaseComputeGate();   // exactly once — the compute gate frees here
            }
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q && /home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -class "*TranscribeCpp*" -notrait "Platform=Windows" 2>&1 | tail -5`
and `/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.IntegrationTests/Winpepper.IntegrationTests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q && /home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.IntegrationTests/bin/Release/net9.0/Winpepper.IntegrationTests.dll -class "*NemotronLayout*" -notrait "Platform=Windows" 2>&1 | tail -5`
Expected: PASS. (The gate tests never reach native code: contract.json checks fire first.)

- [ ] **Step 6: Full suite + commit**

```bash
./scripts/linux-tests.sh    # LINUX SUITE: GREEN
git add src/Winpepper.Asr/TranscribeCpp tests/Winpepper.Asr.Tests tests/Winpepper.IntegrationTests
git commit -m "feat(asr): transcribe.cpp engine with contract/version/ABI gates, CPU backend, PKST streaming and batch run

Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 5: `NemotronStreamingTranscriber` behind the `IStreamingTranscriber` seam

**Files:**
- Create: `src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs`
- Test: `tests/Winpepper.Asr.Tests/Transcription/FakeTranscribeCppEngine.cs`
- Test: `tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs`

**Interfaces:**
- Consumes: `ITranscribeCppEngine`/`ITranscribeCppStream` (Task 4); existing `IStreamingTranscriber`/`IStreamingTranscriptionSession` (`src/Winpepper.Asr/Transcription/IStreamingTranscriber.cs`):
  ```csharp
  public interface IStreamingTranscriptionSession : IAsyncDisposable
  {
      ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct);
      Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct);
  }
  public interface IStreamingTranscriber
  {
      string ModelName { get; }
      Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct);
  }
  ```
  and `ITranscriber.TranscribeAsync(ReadOnlyMemory<float>, CancellationToken) -> Task<TranscriptionResult>`; `TranscriptionResult(string Text, string ProviderModelName)`.
- Produces:
  ```csharp
  public sealed class NemotronStreamingTranscriber : IStreamingTranscriber
  {
      public NemotronStreamingTranscriber(
          Func<ITranscribeCppEngine> engineProvider,   // may throw TranscribeCppException; called on StartSessionAsync
          ITranscriber batchFallback,                  // the TDT ONNX batch transcriber
          string modelName,
          ILogger? log = null,
          int attContextRight = 13);                   // 1040 ms lookahead (spec default)
  }
  ```
  Behavioral contract (the blank-collapse-era guard semantics, applied to the new engine):
  - session feeds in 2560-sample (160 ms) chunks, buffering partial frames;
  - never throws from `PushAsync` (except OCE) — any engine failure latches "corrupt";
  - `FinishAsync` falls back to `batchFallback(fullAudio)` with a LOUD `LogWarning` when: engine/stream creation failed, any feed threw, finalize threw, `WasTruncated`, the final text is null/whitespace, or **zero samples were ever pushed** (the streaming-disabled "late path" calls the seam with zero pushes — go straight to batch, don't spin up a native stream);
  - the transcriber never disposes the engine (the holder owns it); it disposes only its stream.
  - **Dispose is a concurrent abort (verified pipeline behavior):**
    `StreamingDictationSession`/`PipelineHost` call `DisposeAsync` WHILE a
    `PushAsync`/`FinishAsync` may still be in flight (cancel, silence-drop,
    drain-timeout, teardown — StreamingDictationSession.cs:126-138). All native
    stream access (create/feed/finalize/dispose) is therefore serialized under
    a session-level lock, and after dispose the session never touches native
    again: `PushAsync` becomes a no-op, `FinishAsync` goes straight to batch
    fallback. Native calls are short (a 160 ms feed computes in ~20 ms), so a
    plain `lock` is fine; the batch-fallback await happens OUTSIDE the lock.
  - "Committed text growth" note: for this model family, committed text grows
    append-only during speech and tentative stays empty — committed IS the
    partial stream. The existing `IStreamingTranscriptionSession` seam exposes
    no partial-text surface (neither does `ParakeetStreamingSession`), so the
    session consumes the growth internally (`ITranscribeCppStream.Feed`'s
    return value) — the win is that finalize has almost nothing left to do.
    The bench (Task 8/9) proves the committed growth externally via post-stop
    latency ~100–300 ms.

- [ ] **Step 1: Write the fake engine**

```csharp
// tests/Winpepper.Asr.Tests/Transcription/FakeTranscribeCppEngine.cs
using Winpepper.Asr.TranscribeCpp;

namespace Winpepper.Asr.Tests.Transcription;

/// <summary>Scripted fake for the transcribe.cpp engine (the FakeParakeetBackend
/// pattern: streaming logic stays Linux-testable).</summary>
public sealed class FakeTranscribeCppEngine : ITranscribeCppEngine
{
    public string ModelName => "fake-nemotron";
    public int BeginStreamCalls;
    public bool ThrowOnBeginStream;
    public FakeStream? LastStream;

    public string FinalText = "hello world final";
    public bool FinalWasTruncated;
    public bool ThrowOnFeed;
    public bool ThrowOnFinalize;

    public ITranscribeCppStream BeginStream(int attContextRight)
    {
        BeginStreamCalls++;
        if (ThrowOnBeginStream) throw new TranscribeCppException("fake begin failure");
        LastStream = new FakeStream(this) { AttContextRight = attContextRight };
        return LastStream;
    }

    public string TranscribeBatch(float[] mono16k) => "fake-batch";
    public void Dispose() => Disposed = true;
    public bool Disposed;

    public sealed class FakeStream : ITranscribeCppStream
    {
        private readonly FakeTranscribeCppEngine _e;
        public FakeStream(FakeTranscribeCppEngine e) => _e = e;
        public int AttContextRight;
        public readonly List<int> FeedCounts = new();
        public bool Finalized;
        public bool Disposed;

        public string? Feed(float[] samples, int count)
        {
            if (_e.ThrowOnFeed) throw new TranscribeCppException("fake feed failure");
            FeedCounts.Add(count);
            return $"committed after {FeedCounts.Count} feeds";
        }

        public (string Text, bool WasTruncated) Finalize()
        {
            if (_e.ThrowOnFinalize) throw new TranscribeCppException("fake finalize failure");
            Finalized = true;
            return (_e.FinalText, _e.FinalWasTruncated);
        }

        public void Dispose() => Disposed = true;
    }
}
```

- [ ] **Step 2: Write the failing transcriber tests**

Reuse the existing test helpers in `tests/Winpepper.Asr.Tests` for a recording
`ITranscriber` fake and a collecting logger if present (grep for a fake
`ITranscriber` in `FallbackStreamingTranscriberTests.cs` first and reuse it;
otherwise define the small `RecordingBatchTranscriber` below).

```csharp
// tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using Winpepper.Asr.TranscribeCpp;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests.Transcription;

public class NemotronStreamingTranscriberTests
{
    private sealed class RecordingBatchTranscriber : ITranscriber
    {
        public int Calls;
        public string ModelName => "tdt-batch";
        public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> audio, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new TranscriptionResult("batch text", ModelName));
        }
    }

    private static float[] Samples(int n) => new float[n];

    private static NemotronStreamingTranscriber Make(
        FakeTranscribeCppEngine engine, RecordingBatchTranscriber batch)
        => new(() => engine, batch, "nemotron-streaming-en", NullLogger.Instance);

    [Fact]
    public async Task Streams_in_160ms_chunks_and_returns_final_text()
    {
        var engine = new FakeTranscribeCppEngine();
        var batch = new RecordingBatchTranscriber();
        var t = Make(engine, batch);
        await using var s = await t.StartSessionAsync(CancellationToken.None);

        // 8000-sample pre-roll (production's 500 ms) => 3 full 2560 feeds, 320 buffered
        await s.PushAsync(Samples(8000), CancellationToken.None);
        Assert.Equal(new[] { 2560, 2560, 2560 }, engine.LastStream!.FeedCounts);

        // 800-sample frames accumulate: 320 + 800*3 = 2720 => one more feed at the 3rd frame
        await s.PushAsync(Samples(800), CancellationToken.None);
        await s.PushAsync(Samples(800), CancellationToken.None);
        Assert.Equal(3, engine.LastStream.FeedCounts.Count);
        await s.PushAsync(Samples(800), CancellationToken.None);
        Assert.Equal(4, engine.LastStream.FeedCounts.Count);

        var result = await s.FinishAsync(Samples(10400), CancellationToken.None);
        Assert.True(engine.LastStream.Finalized);
        Assert.Equal("hello world final", result.Text);
        Assert.Equal("nemotron-streaming-en", result.ProviderModelName);
        Assert.Equal(0, batch.Calls);
    }

    [Fact]
    public async Task Remainder_is_flushed_before_finalize()
    {
        var engine = new FakeTranscribeCppEngine();
        var t = Make(engine, new RecordingBatchTranscriber());
        await using var s = await t.StartSessionAsync(CancellationToken.None);
        await s.PushAsync(Samples(3000), CancellationToken.None);         // one 2560 feed, 440 left
        await s.FinishAsync(Samples(3000), CancellationToken.None);
        Assert.Equal(new[] { 2560, 440 }, engine.LastStream!.FeedCounts); // tail flushed
    }

    [Fact]
    public async Task Zero_pushed_audio_goes_straight_to_batch_without_a_stream()
    {
        var engine = new FakeTranscribeCppEngine();
        var batch = new RecordingBatchTranscriber();
        var t = Make(engine, batch);
        await using var s = await t.StartSessionAsync(CancellationToken.None);
        var result = await s.FinishAsync(Samples(16000), CancellationToken.None);
        Assert.Equal("batch text", result.Text);
        Assert.Equal(1, batch.Calls);
        Assert.True(engine.LastStream is null || !engine.LastStream.Finalized);
    }

    [Fact]
    public async Task Empty_final_text_falls_back_to_batch()   // blank-collapse-era guard
    {
        var engine = new FakeTranscribeCppEngine { FinalText = "   " };
        var batch = new RecordingBatchTranscriber();
        var t = Make(engine, batch);
        await using var s = await t.StartSessionAsync(CancellationToken.None);
        await s.PushAsync(Samples(4000), CancellationToken.None);
        var result = await s.FinishAsync(Samples(4000), CancellationToken.None);
        Assert.Equal("batch text", result.Text);
        Assert.Equal(1, batch.Calls);
    }

    [Fact]
    public async Task Truncated_stream_falls_back_to_batch()
    {
        var engine = new FakeTranscribeCppEngine { FinalWasTruncated = true };
        var batch = new RecordingBatchTranscriber();
        var t = Make(engine, batch);
        await using var s = await t.StartSessionAsync(CancellationToken.None);
        await s.PushAsync(Samples(4000), CancellationToken.None);
        var result = await s.FinishAsync(Samples(4000), CancellationToken.None);
        Assert.Equal("batch text", result.Text);
    }

    [Fact]
    public async Task Feed_failure_never_throws_from_Push_and_finishes_via_batch()
    {
        var engine = new FakeTranscribeCppEngine { ThrowOnFeed = true };
        var batch = new RecordingBatchTranscriber();
        var t = Make(engine, batch);
        await using var s = await t.StartSessionAsync(CancellationToken.None);
        await s.PushAsync(Samples(4000), CancellationToken.None);   // must NOT throw
        var result = await s.FinishAsync(Samples(4000), CancellationToken.None);
        Assert.Equal("batch text", result.Text);
    }

    [Fact]
    public async Task Finalize_failure_falls_back_to_batch()
    {
        var engine = new FakeTranscribeCppEngine { ThrowOnFinalize = true };
        var batch = new RecordingBatchTranscriber();
        var t = Make(engine, batch);
        await using var s = await t.StartSessionAsync(CancellationToken.None);
        await s.PushAsync(Samples(4000), CancellationToken.None);
        var result = await s.FinishAsync(Samples(4000), CancellationToken.None);
        Assert.Equal("batch text", result.Text);
    }

    [Fact]
    public async Task Engine_provider_failure_still_yields_a_batch_result()
    {
        var batch = new RecordingBatchTranscriber();
        var t = new NemotronStreamingTranscriber(
            () => throw new TranscribeCppException("engine unavailable"),
            batch, "nemotron-streaming-en", NullLogger.Instance);
        await using var s = await t.StartSessionAsync(CancellationToken.None);  // must NOT throw
        await s.PushAsync(Samples(4000), CancellationToken.None);
        var result = await s.FinishAsync(Samples(4000), CancellationToken.None);
        Assert.Equal("batch text", result.Text);
    }

    [Fact]
    public async Task Stream_is_disposed_but_engine_is_not()
    {
        var engine = new FakeTranscribeCppEngine();
        var t = Make(engine, new RecordingBatchTranscriber());
        var s = await t.StartSessionAsync(CancellationToken.None);
        await s.PushAsync(Samples(4000), CancellationToken.None);
        await s.FinishAsync(Samples(4000), CancellationToken.None);
        await s.DisposeAsync();
        Assert.True(engine.LastStream!.Disposed);
        Assert.False(engine.Disposed);
    }

    [Fact]
    public async Task Uses_default_att_context_right_13()
    {
        var engine = new FakeTranscribeCppEngine();
        var t = Make(engine, new RecordingBatchTranscriber());
        await using var s = await t.StartSessionAsync(CancellationToken.None);
        await s.PushAsync(Samples(2560), CancellationToken.None);
        Assert.Equal(13, engine.LastStream!.AttContextRight);
    }

    // Dispose-is-abort contract: the pipeline disposes sessions while pushes
    // may still arrive (cancel / silence-drop / drain-timeout / teardown).
    [Fact]
    public async Task Push_after_dispose_is_a_harmless_no_op()
    {
        var engine = new FakeTranscribeCppEngine();
        var t = Make(engine, new RecordingBatchTranscriber());
        var s = await t.StartSessionAsync(CancellationToken.None);
        await s.PushAsync(Samples(2560), CancellationToken.None);
        await s.DisposeAsync();                                   // pipeline abort path
        await s.PushAsync(Samples(2560), CancellationToken.None); // must NOT throw
        Assert.Single(engine.LastStream!.FeedCounts);             // no native touch after dispose
    }

    [Fact]
    public async Task Finish_after_dispose_falls_back_to_batch_without_native_calls()
    {
        var engine = new FakeTranscribeCppEngine();
        var batch = new RecordingBatchTranscriber();
        var t = Make(engine, batch);
        var s = await t.StartSessionAsync(CancellationToken.None);
        await s.PushAsync(Samples(2560), CancellationToken.None);
        await s.DisposeAsync();
        var result = await s.FinishAsync(Samples(4000), CancellationToken.None);
        Assert.Equal("batch text", result.Text);
        Assert.False(engine.LastStream!.Finalized);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q && /home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -class "*NemotronStreamingTranscriber*" -notrait "Platform=Windows" 2>&1 | tail -5`
Expected: compile FAILURE.

- [ ] **Step 4: Implement**

```csharp
// src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs
using Microsoft.Extensions.Logging;
using Winpepper.Asr.TranscribeCpp;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Real local streaming over transcribe.cpp + nemotron-speech-streaming.
/// One native stream per dictation; feeds 160 ms chunks; committed text grows
/// append-only during speech; finalize at stop returns the final transcript
/// (~100-300 ms). PRESERVES the blank-collapse-era guard posture: ANY engine
/// failure, a truncated stream, an empty final transcript, or a zero-push
/// session falls back to the TDT ONNX batch transcriber with a loud warning.
/// The engine (loaded model) is owned by the caller (NemotronEngineHolder);
/// this class disposes only its per-dictation stream.
/// </summary>
public sealed class NemotronStreamingTranscriber : IStreamingTranscriber
{
    /// <summary>160 ms at 16 kHz — the spike's proven feed size (RTF 0.112 at R=13).</summary>
    internal const int FeedChunkSamples = 2560;

    private readonly Func<ITranscribeCppEngine> _engineProvider;
    private readonly ITranscriber _batchFallback;
    private readonly ILogger? _log;
    private readonly int _attContextRight;

    public NemotronStreamingTranscriber(
        Func<ITranscribeCppEngine> engineProvider,
        ITranscriber batchFallback,
        string modelName,
        ILogger? log = null,
        int attContextRight = 13)
    {
        _engineProvider = engineProvider;
        _batchFallback = batchFallback;
        ModelName = modelName;
        _log = log;
        _attContextRight = attContextRight;
    }

    public string ModelName { get; }

    public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
        => Task.FromResult<IStreamingTranscriptionSession>(
            new Session(_engineProvider, _batchFallback, ModelName, _attContextRight, _log));

    private sealed class Session : IStreamingTranscriptionSession
    {
        private readonly Func<ITranscribeCppEngine> _engineProvider;
        private readonly ITranscriber _batchFallback;
        private readonly string _modelName;
        private readonly int _attContextRight;
        private readonly ILogger? _log;

        private readonly float[] _buffer = new float[FeedChunkSamples];
        // Serializes ALL native stream access. The pipeline disposes sessions
        // as a concurrent abort (cancel/silence-drop/drain-timeout/teardown),
        // so Push/Finish/Dispose can genuinely race — never let two of them
        // touch the native stream at once, and never touch it after dispose.
        private readonly object _nativeGate = new();
        private int _buffered;
        private ITranscribeCppStream? _stream;
        private bool _streamed;   // at least one successful native feed
        private bool _corrupt;
        private string? _corruptReason;
        private bool _disposed;

        public Session(Func<ITranscribeCppEngine> engineProvider, ITranscriber batchFallback,
            string modelName, int attContextRight, ILogger? log)
        {
            _engineProvider = engineProvider;
            _batchFallback = batchFallback;
            _modelName = modelName;
            _attContextRight = attContextRight;
            _log = log;
        }

        public ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_nativeGate)
            {
                if (_disposed || _corrupt) return ValueTask.CompletedTask;
                try
                {
                    EnsureStream();
                    var span = mono16k.Span;
                    var offset = 0;
                    while (offset < span.Length)
                    {
                        var take = Math.Min(FeedChunkSamples - _buffered, span.Length - offset);
                        span.Slice(offset, take).CopyTo(_buffer.AsSpan(_buffered));
                        _buffered += take;
                        offset += take;
                        if (_buffered == FeedChunkSamples)
                        {
                            _stream!.Feed(_buffer, FeedChunkSamples);
                            _streamed = true;
                            _buffered = 0;
                        }
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    MarkCorrupt("push", e);
                }
            }
            return ValueTask.CompletedTask;
        }

        public async Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
        {
            // All native work happens synchronously under the lock; the batch
            // fallback await runs OUTSIDE it (never hold a lock across await).
            string? fallbackReason;
            string finalText = "";
            lock (_nativeGate)
            {
                if (_disposed)
                {
                    fallbackReason = "session was disposed (aborted) before finish";
                }
                else if (!_corrupt && _stream is null && _buffered == 0)
                {
                    // Zero pushed audio (streaming-off "late path", or
                    // all-silence recordings) — no native stream at all.
                    fallbackReason = "no audio was streamed";
                }
                else if (_corrupt)
                {
                    fallbackReason = _corruptReason ?? "streaming failed";
                }
                else
                {
                    try
                    {
                        EnsureStream();
                        if (_buffered > 0)
                        {
                            _stream!.Feed(_buffer, _buffered);   // flush the tail
                            _streamed = true;
                            _buffered = 0;
                        }
                        var (text, truncated) = _stream!.Finalize();
                        if (truncated)
                            fallbackReason = "stream reports was_truncated";
                        else if (string.IsNullOrWhiteSpace(text))
                            fallbackReason = "final streamed transcript is empty";
                        else if (!_streamed)
                            fallbackReason = "no chunk was ever fed";
                        else
                        {
                            fallbackReason = null;
                            finalText = text;
                        }
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        MarkCorrupt("finish", e);
                        fallbackReason = _corruptReason!;
                    }
                }
            }

            if (fallbackReason is not null)
                return await Fallback(fallbackReason, fullAudio, ct).ConfigureAwait(false);
            return new TranscriptionResult(finalText, _modelName);
        }

        public ValueTask DisposeAsync()
        {
            lock (_nativeGate)
            {
                _disposed = true;   // Push/Finish become no-ops / batch-only after this
                try { _stream?.Dispose(); } catch { /* native cleanup must not throw upward */ }
                _stream = null;
            }
            return ValueTask.CompletedTask;
        }

        private void EnsureStream()
        {
            _stream ??= _engineProvider().BeginStream(_attContextRight);
        }

        private void MarkCorrupt(string where, Exception e)
        {
            _corrupt = true;
            _corruptReason = $"{where}: {e.Message}";
            _log?.LogWarning(e, "nemotron streaming failed during {Where}; will fall back to batch", where);
        }

        private async Task<TranscriptionResult> Fallback(string reason, ReadOnlyMemory<float> fullAudio, CancellationToken ct)
        {
            _log?.LogWarning(
                "nemotron streaming fell back to batch transcription: {Reason}. " +
                "Streamed latency win lost for this dictation; transcript comes from the ONNX batch engine.",
                reason);
            return await _batchFallback.TranscribeAsync(fullAudio, ct).ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q && /home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -class "*NemotronStreamingTranscriber*" -notrait "Platform=Windows" 2>&1 | tail -5`
Expected: PASS (12 tests).

- [ ] **Step 6: Full suite + commit**

```bash
./scripts/linux-tests.sh    # LINUX SUITE: GREEN
git add src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs tests/Winpepper.Asr.Tests/Transcription
git commit -m "feat(asr): NemotronStreamingTranscriber behind the IStreamingTranscriber seam with full batch-fallback guard

Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 6: Wiring — engine holder, `AppShell.BuildStreamingTranscriber`, `StreamingEnabled` default

**Files:**
- Create: `src/Winpepper.App/Hosting/NemotronEngineHolder.cs`
- Modify: `src/Winpepper.App/Hosting/AppShell.cs` (method at `:420`, doc comment `:407`, injection closure `:284-286`)
- Modify: `src/Winpepper.Core/Settings/AppSettings.cs:53` (default + doc comment)
- Test: `tests/Winpepper.Core.Tests/AppSettingsDefaultsTests.cs` (update expectation)

**Interfaces:**
- Consumes: `TranscribeCppEngine.Load`, `NemotronStreamingModel.IsInstalled/GgufPath/RuntimeDir`, `NemotronStreamingTranscriber`, existing `BatchStreamingAdapter` (read `src/Winpepper.Asr/Transcription/BatchStreamingAdapter.cs` for its exact ctor — it wraps an `ITranscriber`), `ParakeetTranscriber`.
- Produces: `NemotronEngineHolder` with `public ITranscribeCppEngine? TryGet()` (thread-safe, lazy, caches success forever and failure until files change is NOT attempted — failure latches for process lifetime with one loud error log). `BuildStreamingTranscriber` gains one parameter: `Func<Winpepper.Asr.TranscribeCpp.ITranscribeCppEngine?>? nemotronEngine`. The `PipelineHost` delegate signature (`Func<ParakeetSession, string, AppSettings, Action<string>, IStreamingTranscriber>`) is UNCHANGED — the AppShell.Create closure supplies the new argument.

**Sequencing note (load-bearing review):** the `StreamingEnabled` default flip
(Step 2) and the local-branch rewiring (Step 4) land in this SAME task/commit
deliberately. Today's local ON branch still constructs the proven-broken
chunked-TDT `ParakeetStreamingTranscriber` (`AppShell.cs:434-439`) — flipping
the default in an earlier commit would enable that path for every local user
until the rewire lands. Do not split these steps across commits.

- [ ] **Step 1: Update the settings default test (failing first)**

In `tests/Winpepper.Core.Tests/AppSettingsDefaultsTests.cs`, find the
`StreamingEnabled` default assertion and flip it to expect `true`. Also check
`tests/Winpepper.Core.Tests/Settings/StreamingSettingPersistenceTests.cs` for
hardcoded default expectations and update in the same way.

Run: `/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q && /home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -class "*AppSettings*" -notrait "Platform=Windows" 2>&1 | tail -5`
Expected: FAIL (default is still `false`).

- [ ] **Step 2: Flip the default with an honest doc comment**

Replace the `StreamingEnabled` block in `src/Winpepper.Core/Settings/AppSettings.cs`
(currently `:53` with the OFF-BY-DEFAULT comment) with:

```csharp
    // Streaming transcription: transcribe audio while you speak so the text is
    // (nearly) ready the moment you stop. Applies to BOTH providers. Read LIVE
    // per dictation by PipelineHost, so a flip takes effect on the very next
    // dictation. ON BY DEFAULT (2026-07-25): safe in every configuration —
    // AssemblyAI streams with local batch fallback; local WITH the Nemotron
    // streaming model streams via transcribe.cpp with batch fallback on any
    // failure; local WITHOUT it uses BatchStreamingAdapter (identical to the
    // old default: one batch transcription at stop). The chunked-TDT streaming
    // attempt (blank-collapse defect, see 2026-07-25 streaming verification
    // evidence) is no longer wired for local dictations.
    public bool StreamingEnabled { get; init; } = true;
```

Run the same test filter — expected: PASS.

- [ ] **Step 3: Implement `NemotronEngineHolder`**

```csharp
// src/Winpepper.App/Hosting/NemotronEngineHolder.cs
#if WINDOWS
using Microsoft.Extensions.Logging;
using Winpepper.Asr.TranscribeCpp;

namespace Winpepper.App.Hosting;

/// <summary>
/// Process-wide lazy holder for the transcribe.cpp engine. The ~0.9 s model
/// load happens once, on the first streaming dictation after install; the
/// model handle is never freed (no dispose race with in-flight dictations, no
/// OrphanedPumpGuard involvement). Not-installed is re-checked every call so
/// installing the model takes effect without a restart; a LOAD FAILURE latches
/// null for the process lifetime (one loud error, no retry storm).
/// </summary>
public sealed class NemotronEngineHolder
{
    private readonly string _modelsRoot;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private ITranscribeCppEngine? _engine;
    private bool _failedPermanently;

    public NemotronEngineHolder(string modelsRoot, ILogger log)
    {
        _modelsRoot = modelsRoot;
        _log = log;
    }

    public ITranscribeCppEngine? TryGet()
    {
        lock (_gate)
        {
            if (_engine is not null) return _engine;
            if (_failedPermanently) return null;
            if (!NemotronStreamingModel.IsInstalled(_modelsRoot)) return null;
            try
            {
                _engine = TranscribeCppEngine.Load(
                    NemotronStreamingModel.RuntimeDir(_modelsRoot),
                    NemotronStreamingModel.GgufPath(_modelsRoot),
                    msg => _log.LogWarning("{TranscribeCppLog}", msg));
                _log.LogInformation("transcribe.cpp engine loaded ({Model})", _engine.ModelName);
                return _engine;
            }
            catch (Exception e)
            {
                _failedPermanently = true;
                _log.LogError(e,
                    "transcribe.cpp engine failed to load — local streaming disabled for this run; " +
                    "dictations use batch transcription (contract/ABI/model problem, see exception)");
                return null;
            }
        }
    }
}
#endif
```

(Match the file-level `#if WINDOWS` pattern used by `PipelineHost.cs:1`; if
other `Hosting/*.cs` files in this project compile without the guard because
the whole project is Windows-only, follow whichever pattern `AppShell.cs`
itself uses.)

- [ ] **Step 4: Wire `BuildStreamingTranscriber`**

In `src/Winpepper.App/Hosting/AppShell.cs`:

1. Add the parameter after `onFallback` in the signature at `:420` (doc comment `:407`):
   ```csharp
   Func<Winpepper.Asr.TranscribeCpp.ITranscribeCppEngine?>? nemotronEngine,
   ```
2. Replace the local-branch body (currently constructs
   `ParakeetStreamingTranscriber` and `return localStreaming;` when the
   provider is not assemblyai) with:
   ```csharp
   var localBatch = new Winpepper.Asr.Transcription.ParakeetTranscriber(
       local, loadedModelName);

   if (!string.Equals(settings.AsrProvider, "assemblyai", StringComparison.OrdinalIgnoreCase))
   {
       // Local streaming: REAL streaming only via transcribe.cpp + the
       // Nemotron streaming model. The chunked-TDT ParakeetStreamingTranscriber
       // is deliberately NOT wired anymore: it cannot stream (blank-collapse,
       // see docs/plans/2026-07-25-streaming-verification-evidence.md) and its
       // guard carries a residual false-negative risk. Without the engine we
       // go straight to the batch adapter — same result, no doomed attempt.
       var engine = nemotronEngine?.Invoke();
       if (engine is not null)
       {
           return new Winpepper.Asr.Transcription.NemotronStreamingTranscriber(
               () => engine, localBatch,
               Winpepper.Asr.TranscribeCpp.NemotronStreamingModel.Name,
               loggerFactory.CreateLogger<Winpepper.Asr.Transcription.NemotronStreamingTranscriber>());
       }
       return new Winpepper.Asr.Transcription.BatchStreamingAdapter(localBatch);
   }
   ```
   (Check `BatchStreamingAdapter`'s actual ctor arguments in
   `src/Winpepper.Asr/Transcription/BatchStreamingAdapter.cs` — it is 33 lines;
   if it takes additional args (e.g. a model name), pass `loadedModelName`.)
   Update the method's `<summary>` doc comment to describe the new branch.
3. In `AppShell.Create`, construct the holder once near the ModelsServices
   wiring (`AppShell.cs:67-76` — reuse the same models root expression,
   `Path.Combine(AppPaths.Root, "models")` / `modelsServices.ModelsRoot`):
   ```csharp
   var nemotronHolder = new NemotronEngineHolder(
       modelsServices.ModelsRoot, factory.CreateLogger<NemotronEngineHolder>());
   ```
   and thread `() => nemotronHolder.TryGet()` into the delegate at `:284-286`:
   ```csharp
   (local, loadedModelName, s, onFallback) => AppShell.BuildStreamingTranscriber(
       local, loadedModelName, s, onFallback, () => nemotronHolder.TryGet(),
       aaiClient, aaiKeyStore, aaiOptions, correctionStore, errorBus, factory),
   ```
   (parameter order must match your edit in item 1).
4. `grep -rn "BuildStreamingTranscriber" src/ tests/` and update every other
   call site the same way (pass `null` for `nemotronEngine` where no holder
   exists, e.g. in any tests — `null` means "engine unavailable" and preserves
   old batch-adapter behavior).

- [ ] **Step 5: Cross-compile check + full suite + commit**

The App project is Windows-only and cannot build on Linux — the Linux suite
covers `Winpepper.Asr`/`Core`/`Models`; the App compiles in Task 10's
windows-gate. Still, run a fast syntax sanity pass on the touched shared
projects:

```bash
/home/dan/code/winpepper/.dotnet/dotnet build src/Winpepper.Asr -f net9.0 && \
/home/dan/code/winpepper/.dotnet/dotnet build src/Winpepper.Core -f net9.0
./scripts/linux-tests.sh    # LINUX SUITE: GREEN
git add src/Winpepper.App src/Winpepper.Core tests/Winpepper.Core.Tests
git commit -m "feat(app): wire transcribe.cpp streaming into local dictations; StreamingEnabled defaults ON

Local streaming now means: Nemotron engine when installed (batch fallback on
any failure), BatchStreamingAdapter otherwise. The chunked-TDT streaming
attempt is unwired from production (kept for bench/tests). Documented decision:
see docs/plans/2026-07-25-nemotron-streaming.md.

Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

Note: an early Windows compile check of `Winpepper.App` here is allowed but
optional (`powershell.exe ... dotnet build` over the UNC path, foreground); the
hard gate is Task 10. If you run it, respect the concurrent-builds poll from
Task 9 Step 1.

---

### Task 7: Models page — streaming model card + honest captions

**Files:**
- Modify: `src/Winpepper.Models/ViewModels/ModelsTabViewModel.cs`
- Modify: `src/Winpepper.App/Views/ModelsPage.xaml` (+ `.xaml.cs`)
- Test: `tests/Winpepper.Models.Tests/ModelsTabViewModelStreamingTests.cs`

**Interfaces:**
- Consumes: `ModelRegistry.StreamingAsrName`, `ModelKind.StreamingAsr`, the existing card/progress machinery (`ModelsTabViewModel.DownloadMissingAsync` at `ModelsTabViewModel.cs:48-96`, `ModelProvisioningCoordinator`, `DownloadProgress`), existing page wiring (`ModelsPage.xaml.cs:31-62` for registry/root access, `:200-203` for the streaming toggle).
- Produces: `ModelsTabViewModel.StreamingCard` (same card type as `CleanupCard`) listing the `StreamingAsr` descriptor; `Task DownloadStreamingAsync()` that downloads exactly the nemotron descriptor through the same coordinator/downloader path `DownloadMissingAsync` uses, routing progress to `StreamingCard`.

**Before writing code:** read `src/Winpepper.Models/ViewModels/ModelsTabViewModel.cs`
in full (it is small) and mirror its existing structures exactly — same card
class, same progress plumbing, same async/error conventions. The steps below
specify the behavior contract; the existing file dictates the idioms.

- [ ] **Step 1: Write the failing ViewModel tests**

`Winpepper.Models` is net9.0 — these run on Linux. Mirror the construction
pattern of the existing `ModelsTabViewModel` tests (find them via
`grep -rln "ModelsTabViewModel" tests/`); if none exist, construct the VM the
way `ModelsPage.xaml.cs` does.

```csharp
// tests/Winpepper.Models.Tests/ModelsTabViewModelStreamingTests.cs
using Winpepper.Models;
using Winpepper.Models.ViewModels;
using Xunit;

namespace Winpepper.Models.Tests;

public class ModelsTabViewModelStreamingTests
{
    [Fact]
    public void StreamingCard_lists_exactly_the_nemotron_descriptor()
    {
        var vm = /* construct as the existing VM tests / ModelsPage do */;
        var names = vm.StreamingCard.Available.Select(d => d.Name).ToList();
        Assert.Equal(new[] { ModelRegistry.StreamingAsrName }, names);
    }

    [Fact]
    public void Streaming_descriptor_is_not_in_the_batch_asr_card()
    {
        var vm = /* same construction */;
        Assert.DoesNotContain(vm.AsrCard.Available, d => d.Kind == ModelKind.StreamingAsr);
    }
}
```

(Replace the construction comments with the real pattern found in the repo —
that is a read-the-file step, not a placeholder: the VM ctor takes the registry
plus service dependencies visible at `ModelsTabViewModel.cs:37-42`.)

- [ ] **Step 2: Run to verify failure**

Run: `/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q && /home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Models.Tests/bin/Release/net9.0/Winpepper.Models.Tests.dll -class "*ModelsTabViewModelStreaming*" -notrait "Platform=Windows" 2>&1 | tail -5`
Expected: compile FAILURE (`StreamingCard` missing).

- [ ] **Step 3: Implement the ViewModel additions**

In `ModelsTabViewModel.cs`:
- Add `StreamingCard` (same type as `CleanupCard`), sourced from
  `registry.ByKind(ModelKind.StreamingAsr)`.
- Fix the two-way kind routing from Task 3 Step 4 so `StreamingAsr` progress
  goes to `StreamingCard` (a `switch` on `d.Kind`).
- Add `DownloadStreamingAsync()`: same body shape as `DownloadMissingAsync`
  (`:48-96`) but the descriptor set is exactly
  `new[] { registry.Find(ModelRegistry.StreamingAsrName)! }.Where(d => !d.IsFullyInstalled(modelsRoot))`.

Run the Step 1 tests — expected: PASS.

- [ ] **Step 4: Add the page card + update the toggle caption**

In `src/Winpepper.App/Views/ModelsPage.xaml`, after the streaming toggle block
(`:37-44`), replace the current caption `TextBlock` (the one saying local
"falls back ... (no speed benefit)") with:

```xml
                    <TextBlock Text="Starts transcribing while you dictate so text is ready almost as soon as you stop. Works with AssemblyAI out of the box. For the local provider, install the Nemotron streaming model below to get real live streaming; without it, dictations are transcribed in one pass after you stop (same results, just slower to appear)."
                               TextWrapping="Wrap"
                               Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                               Style="{ThemeResource CaptionTextBlockStyle}" />
```

Then add a third card `<Border>` after the Cleanup card (`:134-160`), copying
the Cleanup card's exact structure (installed icon + label, per-file progress
`ListView`) with:
- header text: `Live streaming model (optional)`
- caption `TextBlock`:
  ```xml
  <TextBlock Text="Nemotron Speech Streaming 0.6B (English only) with the transcribe.cpp runtime — about 720 MB. Enables live local streaming when 'Transcribe while you speak' is on. After your first streaming dictation the model stays loaded (about 1 GB of memory) until you close Winpepper. Model weights are under the NVIDIA Open Model License; downloaded from Hugging Face / GitHub on request. Requires the Microsoft Visual C++ x64 runtime (preinstalled on most PCs)."
             TextWrapping="Wrap"
             Foreground="{ThemeResource TextFillColorSecondaryBrush}"
             Style="{ThemeResource CaptionTextBlockStyle}" />
  ```
- an install `Button` (`x:Name="StreamingModelInstallButton"`,
  `AutomationProperties.AutomationId="StreamingModelInstallButton"`, content
  `Install streaming model`) wired in `ModelsPage.xaml.cs` next to the existing
  download wiring:
  ```csharp
  StreamingModelInstallButton.Click += async (_, _) => await ViewModel.DownloadStreamingAsync();
  ```
- installed-state label driven by
  `models.Registry.Find(ModelRegistry.StreamingAsrName)!.IsFullyInstalled(models.ModelsRoot)`
  refreshed the same way/places the Cleanup card refreshes its installed state
  (grep the code-behind for where the Cleanup installed label is set and mirror
  it, including the after-download refresh).

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh    # LINUX SUITE: GREEN
git add src/Winpepper.Models src/Winpepper.App/Views tests/Winpepper.Models.Tests
git commit -m "feat(ui): installable Nemotron streaming model card; honest streaming captions

Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

(XAML correctness is verified by the windows-gate App build in Task 10 and the
bench-adjacent manual-equivalent check in Task 9.)

---

### Task 8: Bench — `real-nemotron-stream` scenario + Windows driver script

**Files:**
- Modify: `scripts/asr-latency-bench/Program.cs`
- Create: `scripts/run-nemotron-bench-windows.sh` (`chmod +x`)

**Interfaces:**
- Consumes: `TranscribeCppEngine.Load`, `NemotronStreamingTranscriber`, `StreamingDictationSession`, `TranscriptDiff.Summarize`, `BenchAudio`, existing arg loop (`Program.cs:16-46`), scenario switch (`:49-205`), `CollectingLogger` (`:365`), row list shape `(string Scenario, string Kind, double AudioSeconds, long PostStopMs)`.
- Produces: scenario name `real-nemotron-stream`; new args `--nemotron-model <gguf path>`, `--nemotron-runtime <dll dir>`; console diagnostics `# nem-batch[...]`, `# nem-stream[...]`, `# diff-parity[...]` (streamed-nemotron vs batch-nemotron — THE acceptance bar), `# diff-vs-tdt[...]` (characterization vs TDT batch, when `--model-dir` given).

- [ ] **Step 1: Add the args**

In the arg loop (`Program.cs:26-36`), add:

```csharp
        case "--nemotron-model": nemotronModel = args[++argIdx]; break;
        case "--nemotron-runtime": nemotronRuntime = args[++argIdx]; break;
```
with declarations next to `modelDir`:
```csharp
string? nemotronModel = null;
string? nemotronRuntime = null;
```

- [ ] **Step 2: Add the scenario**

Insert a new case in the switch (style copied from `real-local`,
`Program.cs:138-200`):

```csharp
        case "real-nemotron-stream":
        {
            if (nemotronModel is null || nemotronRuntime is null || wavPaths.Count == 0)
            {
                Console.WriteLine("real-nemotron-stream: SKIPPED (requires --nemotron-model, --nemotron-runtime and at least one --wav)");
                break;
            }
            if (!File.Exists(nemotronModel) || !File.Exists(Path.Combine(nemotronRuntime, "transcribe.dll")))
            {
                Console.WriteLine($"real-nemotron-stream: SKIPPED (model or runtime not found)");
                break;
            }
            using var engine = Winpepper.Asr.TranscribeCpp.TranscribeCppEngine.Load(
                nemotronRuntime, nemotronModel, msg => Console.WriteLine($"# nem-log: {msg}"));
            Console.WriteLine($"# real-nemotron-stream: engine loaded (CPU backend, att_context_right=13)");

            // Optional TDT batch reference for the quality characterization.
            ParakeetSession? tdtSession = null;
            ParakeetTranscriber? tdtBatch = null;
            if (modelDir is not null && ParakeetSession.ModelFilesPresent(modelDir))
            {
                tdtSession = new ParakeetSession(modelDir);
                tdtBatch = new ParakeetTranscriber(tdtSession, "parakeet-tdt-0.6b-v3");
            }
            try
            {
                foreach (var wavPath in wavPaths)
                {
                    var name = Path.GetFileName(wavPath);
                    var wavAudio = BenchAudio.Prepare(BenchAudio.ReadMono16k(wavPath), gain, leadSilenceMs);
                    var seconds = wavAudio.Length / 16000.0;

                    // (a) nemotron BATCH — the parity reference (same engine, offline).
                    var swNb = Stopwatch.StartNew();
                    var nemBatchText = engine.TranscribeBatch(wavAudio);
                    swNb.Stop();
                    rows.Add(($"nem-batch {name}", "REAL nemotron", seconds, swNb.ElapsedMilliseconds));
                    Console.WriteLine($"# nem-batch[{name}]: \"{nemBatchText}\"");

                    // (b) nemotron STREAMED through the REAL production stack:
                    // NemotronStreamingTranscriber inside StreamingDictationSession,
                    // 50 ms frames at real-time pace (the manual-equivalent check).
                    var fellBack = false;
                    var fallbackProbe = new ProbeTranscriber(() => fellBack = true, tdtBatch);
                    var streaming = new NemotronStreamingTranscriber(
                        () => engine, fallbackProbe, "nemotron-streaming-en");
                    await using var session = StreamingDictationSession.Start(
                        _ => Task.FromResult<IStreamingTranscriber?>(streaming),
                        NullLogger.Instance, CancellationToken.None, TimeSpan.FromSeconds(10));
                    const int frame = 800; // 50 ms
                    for (var i = 0; i < wavAudio.Length; i += frame)
                    {
                        session.OnFrame(wavAudio.AsMemory(i, Math.Min(frame, wavAudio.Length - i)));
                        await Task.Delay(50);
                    }
                    var swStream = Stopwatch.StartNew();
                    var streamResult = await session.FinishAsync(wavAudio, CancellationToken.None);
                    swStream.Stop();
                    if (streamResult is null) throw new InvalidOperationException("no transcript from coordinator");
                    rows.Add(($"nem-stream {name}", "REAL nemotron", seconds, swStream.ElapsedMilliseconds));
                    Console.WriteLine($"# nem-stream[{name}]: fellBackToBatch={fellBack} \"{streamResult.Text}\"");

                    // (c) parity bar: streamed-nemotron == batch-nemotron.
                    var parity = TranscriptDiff.Summarize(nemBatchText, streamResult.Text);
                    Console.WriteLine($"# diff-parity[{name}]: {parity.Describe()}");

                    // (d) honest characterization vs the TDT ONNX batch engine.
                    if (tdtBatch is not null)
                    {
                        var tdtText = (await tdtBatch.TranscribeAsync(wavAudio, CancellationToken.None)).Text;
                        Console.WriteLine($"# tdt-batch[{name}]: \"{tdtText}\"");
                        var vsTdt = TranscriptDiff.Summarize(tdtText, streamResult.Text);
                        Console.WriteLine($"# diff-vs-tdt[{name}]: {vsTdt.Describe()}");
                    }
                }
            }
            finally
            {
                tdtSession?.Dispose();
            }
            break;
        }
```

And the probe fake next to the other file-local fakes (`Program.cs:259+`):

```csharp
sealed class ProbeTranscriber : ITranscriber
{
    private readonly Action _onCalled;
    private readonly ITranscriber? _inner;
    public ProbeTranscriber(Action onCalled, ITranscriber? inner) { _onCalled = onCalled; _inner = inner; }
    public string ModelName => _inner?.ModelName ?? "fallback-probe";
    public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> audio, CancellationToken ct)
    {
        _onCalled();
        return _inner is not null
            ? _inner.TranscribeAsync(audio, ct)
            : Task.FromResult(new TranscriptionResult("", ModelName));
    }
}
```

Add any missing `using` lines to match (the file already uses top-level
statements with `ParakeetSession`, `Stopwatch`, `NullLogger` — check the top of
the file).

Compute-gate caution (v0.1.3 one-compute-per-model rule): `TranscribeBatch` and
the streaming session share the engine's compute gate. The scenario above is
safe because each leg finishes before the next starts and `await using` frees
the stream (releasing the gate) at the end of each iteration — keep it that
way; a leaked stream would make the next `TranscribeBatch` throw a gate-timeout
`TranscribeCppException` rather than corrupt, but the bench run would be wasted.

- [ ] **Step 3: Verify it builds on Linux**

Run: `/home/dan/code/winpepper/.dotnet/dotnet build scripts/asr-latency-bench/AsrLatencyBench.csproj 2>&1 | tail -3`
Expected: `Build succeeded`.

- [ ] **Step 4: Write `scripts/run-nemotron-bench-windows.sh`**

Model it on `scripts/run-bench-windows.sh` (same `ps_run` helper, same staging
rationale). Full script:

```bash
#!/usr/bin/env bash
# Build the ASR latency bench with the Windows dotnet, stage it to a
# Windows-local %TEMP% dir (native library loads from UNC are unreliable),
# generate the reference TTS WAVs, and run real-nemotron-stream over the four
# phrase categories using the spike's already-downloaded model + runtime at
# %LOCALAPPDATA%\Temp\transcribe-spike (read-only reuse; the SHIPPED
# acquisition path still downloads fresh with pinned hashes).
#
# Host safety: only host writes are %TEMP% staging dirs and NuGet restore.
# Reads (never writes) the spike scratch and the installed TDT model dir.
# Never touches a running Winpepper.exe or %LOCALAPPDATA%\winpepper.
#
# Usage: ./scripts/run-nemotron-bench-windows.sh
# Output: artifacts/nemotron-bench/<category>.log
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

PS="/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
[[ -x "$PS" ]] || { echo "run-nemotron-bench-windows: powershell.exe not found at $PS" >&2; exit 2; }

UNC_ROOT="$(wslpath -w "$HERE")"
TDT_MODEL_DIR='C:\Users\dan\AppData\Local\winpepper\models\parakeet-tdt-0.6b-v3'
NEM_MODEL='C:\Users\dan\AppData\Local\Temp\transcribe-spike\nemotron-speech-streaming-en-0.6b-Q8_0.gguf'
NEM_RUNTIME='C:\Users\dan\AppData\Local\Temp\transcribe-spike\transcribe-native-windows-x86_64-cpu-vulkan'
OUT="$HERE/artifacts/nemotron-bench"
mkdir -p "$OUT"

ps_run() { # ps_run <timeout_s> <logfile> <ps-command>
  local t="$1" log="$2" cmd="$3"
  timeout --foreground "$t" "$PS" -NoProfile -ExecutionPolicy Bypass \
    -Command "$cmd; exit \$LASTEXITCODE" 2>&1 | tee "$log"
  return "${PIPESTATUS[0]}"
}

echo "=== [1/4] Build bench (Windows dotnet, Release) ==="
bench_csproj="$UNC_ROOT"'\scripts\asr-latency-bench\AsrLatencyBench.csproj'
ps_run 1800 "$OUT/build.log" "dotnet build '$bench_csproj' -c Release"

echo "=== [2/4] Stage bench output to %TEMP%\\winpepper-nemotron-bench ==="
bench_bin="$UNC_ROOT"'\scripts\asr-latency-bench\bin\Release\net9.0'
ps_run 300 "$OUT/stage.log" "
  \$dst = Join-Path \$env:TEMP 'winpepper-nemotron-bench'
  if (Test-Path \$dst) { Remove-Item -Recurse -Force \$dst }
  Copy-Item -Recurse '$bench_bin' \$dst"

echo "=== [3/4] Generate TTS WAVs on the host ==="
gen_script="$UNC_ROOT"'\scripts\generate-bench-wavs.ps1'
ps_run 300 "$OUT/tts.log" "& '$gen_script' -OutDir (Join-Path \$env:TEMP 'winpepper-bench-wavs')"

echo "=== [4/4] real-nemotron-stream, four phrase categories ==="
run_category() { # run_category <name> <wav> [extra bench args...]
  local name="$1" wav="$2"; shift 2
  echo "--- $name ---"
  ps_run 1800 "$OUT/$name.log" "
    Set-Location (Join-Path \$env:TEMP 'winpepper-nemotron-bench')
    dotnet exec AsrLatencyBench.dll real-nemotron-stream \
      --nemotron-model '$NEM_MODEL' --nemotron-runtime '$NEM_RUNTIME' \
      --model-dir '$TDT_MODEL_DIR' \
      --wav (Join-Path \$env:TEMP 'winpepper-bench-wavs\\$wav') $*"
}
run_category normal        normal-10s.wav
run_category pause-mid     pause-mid.wav
run_category quiet         normal-10s.wav --gain 0.02
run_category lead-silence  normal-10s.wav --lead-silence-ms 1500

echo "run-nemotron-bench-windows: done -- logs in $OUT"
```

`chmod +x scripts/run-nemotron-bench-windows.sh`.

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh    # LINUX SUITE: GREEN
git add scripts/asr-latency-bench scripts/run-nemotron-bench-windows.sh
git commit -m "feat(bench): real-nemotron-stream scenario (streamed vs nemotron-batch parity, TDT characterization, post-stop latency)

Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 9: Run the bench on Windows and write the evidence doc (REAL numbers)

**Files:**
- Create: `docs/plans/2026-07-25-nemotron-streaming-evidence.md`
- Artifacts (untracked is fine, mirror existing bench conventions): `artifacts/nemotron-bench/*.log`

**Interfaces:**
- Consumes: Task 8's script; the spike scratch dir; the four category logs.
- Produces: the committed evidence doc that Task 10's gate section is appended to.

- [ ] **Step 1: Wait for a quiet host**

Other agent sessions sometimes run Windows builds concurrently. Poll until no
`dotnet.exe` with `winpepper` in its command line exists — two consecutive
zero polls 45 s apart:

```bash
PS="/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
check() { timeout --foreground 60 "$PS" -NoProfile -Command \
  "(Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" | Where-Object { \$_.CommandLine -match 'winpepper' } | Measure-Object).Count"; }
while true; do
  c1=$(check | tr -d '[:space:]'); sleep 45; c2=$(check | tr -d '[:space:]')
  echo "poll: $c1 then $c2"
  [[ "$c1" == "0" && "$c2" == "0" ]] && break
  sleep 60
done
```

- [ ] **Step 2: Verify the spike scratch is intact (read-only)**

```bash
ls -la /mnt/c/Users/dan/AppData/Local/Temp/transcribe-spike/
ls /mnt/c/Users/dan/AppData/Local/Temp/transcribe-spike/transcribe-native-windows-x86_64-cpu-vulkan/ | head
```
Expected: the GGUF (729,650,176 bytes) and the extracted runtime dir with
`transcribe.dll` + `contract.json`. If missing, re-create it in `%TEMP%` by
downloading the two pinned URLs (Global Constraints: big downloads to `%TEMP%`,
generous timeouts) and extracting the tarball with `tar -xzf` — do NOT touch
`%LOCALAPPDATA%\winpepper`.

- [ ] **Step 3: Run the bench (foreground, generous timeout)**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-streaming
timeout --foreground 7200 ./scripts/run-nemotron-bench-windows.sh
```
Expected: four category logs in `artifacts/nemotron-bench/`, each containing
`# nem-batch[...]`, `# nem-stream[...]: fellBackToBatch=False "..."`,
`# diff-parity[...]`, `# tdt-batch[...]`, `# diff-vs-tdt[...]`, and the final
markdown latency table. If `fellBackToBatch=True` anywhere, the run does NOT
count as streamed — debug before writing evidence (check `# nem-log:` lines).
First run may pay NuGet restore + model load; the per-category runs are
minutes each.

- [ ] **Step 4: Write the evidence doc**

Follow the structure of `docs/plans/2026-07-25-streaming-verification-evidence.md`
(Method → Results table → verbatim artifacts → Acceptance assessment →
Windows pre-push gate result → Cross-references & environment honesty). Create
`docs/plans/2026-07-25-nemotron-streaming-evidence.md` with:

- **Method**: harness = `scripts/run-nemotron-bench-windows.sh` →
  `AsrLatencyBench real-nemotron-stream`; host CPU; model = Q8_0 GGUF
  (SHA-256 pinned in `ModelRegistry`); runtime = transcribe.cpp v0.1.3 CPU
  backend, `att_context_right=13`; streamed path runs the REAL production
  stack (`StreamingDictationSession` + `NemotronStreamingTranscriber`) at
  real-time 50 ms pacing — the manual-equivalent, end-to-end check. Honesty
  caveats: TTS-generated speech, spike-scratch binaries reused (hashes match
  the pinned registry values).
- **Results**: one table row per category — columns: category, audio s,
  nem-batch post-stop ms, nem-stream post-stop ms, parity diff word count,
  vs-TDT diff word count, `fellBackToBatch`. COPY THE REAL NUMBERS from the
  logs; never round-trip from memory.
- Verbatim fenced blocks: for each category, the `# nem-batch`, `# nem-stream`,
  `# diff-parity`, `# tdt-batch`, `# diff-vs-tdt` lines with a
  `(artifacts/nemotron-bench/<name>.log)` parenthetical.
- **Acceptance assessment** (bold verdicts, failures stated plainly):
  1. streamed-nemotron == batch-nemotron word-level (after `TranscriptDiff`
     normalization) on all four categories — the parity bar;
  2. post-stop latency < 500 ms for the 10 s phrase (`normal` category
     `nem-stream` row);
  3. nemotron vs TDT-batch wording differences characterized honestly (list
     the actual diff words; nemotron may legitimately differ from TDT);
  4. no category fell back to batch.
- **Windows pre-push gate result**: placeholder sentence "appended by Task 10"
  (Task 10 fills it — the doc is committed twice by design).
- **Cross-references**: this plan; the 2026-07-25 streaming-verification
  evidence (what this replaces); spike source path.

If a criterion FAILS: still record the real numbers, state the failure
plainly in the assessment, and stop for review rather than tuning numbers —
the whole point of this effort is honest evidence.

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh    # LINUX SUITE: GREEN
git add docs/plans/2026-07-25-nemotron-streaming-evidence.md
git commit -m "docs(evidence): real nemotron streamed-vs-batch transcripts and post-stop latency on four phrase categories

Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 10: Licensing, README, and the Windows gate

**Files:**
- Create: `THIRD-PARTY-NOTICES.md`
- Modify: `README.md` (model section, `:50-64`)
- Modify: `docs/plans/2026-07-25-nemotron-streaming-evidence.md` (gate section)

**Interfaces:**
- Consumes: everything committed so far; the tarball's bundled `licenses/` tree (visible in the spike scratch extracted dir) for the exact MIT texts.
- Produces: the finished branch.

- [ ] **Step 1: Write `THIRD-PARTY-NOTICES.md`**

```markdown
# Third-party notices

Winpepper itself is licensed under the Apache License 2.0 (see LICENSE).
The components below are NOT distributed inside the Winpepper installer;
they are downloaded on the user's request from the pinned sources recorded
in `src/Winpepper.Models/ModelRegistry.cs` (URL + SHA-256 verified).

## transcribe.cpp (native runtime, downloaded at user request)

- Project: https://github.com/handy-computer/transcribe.cpp — version v0.1.3
- License: MIT
- The runtime archive bundles ggml (MIT) and other MIT-licensed components;
  the archive ships its complete license texts under `licenses/` and they are
  installed verbatim to
  `%LOCALAPPDATA%\winpepper\models\nemotron-streaming-en\runtime\transcribe-native-windows-x86_64-cpu-vulkan\licenses\`.

[paste the MIT license text from the tarball's licenses/LICENSE here, verbatim:
/mnt/c/Users/dan/AppData/Local/Temp/transcribe-spike/transcribe-native-windows-x86_64-cpu-vulkan/licenses/LICENSE]

## Nemotron Speech Streaming model weights (downloaded at user request)

- Model: nvidia/nemotron-speech-streaming-en-0.6b, GGUF Q8_0 conversion by
  handy-computer (https://huggingface.co/handy-computer/nemotron-speech-streaming-en-0.6b-gguf)
- License: NVIDIA Open Model License ("license: other" on Hugging Face) —
  https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-open-model-license/
- Licensed by NVIDIA Corporation under the NVIDIA Open Model License
- The weights are not redistributed by this project. Users download them
  directly from Hugging Face via the Models tab; the License provides that by
  using, reproducing, or distributing any portion of the Model you agree to be
  bound by the Agreement (acceptance by conduct — no click-through is
  required). The attribution line above and this link to the Agreement are
  included preemptively to satisfy the License's Section 3 notice condition in
  case facilitating the download is ever characterized as distribution. The
  License may be updated by NVIDIA; the live URL above is authoritative.

## Existing model downloads

The Parakeet-TDT ONNX models and the Qwen cleanup model are likewise
downloaded at user request from the sources in `ModelRegistry.cs` under their
respective upstream licenses.
```

(Do paste the actual MIT text from the tarball — the bracketed line is an
instruction, not content to keep.)

- [ ] **Step 2: Update the README model section honestly**

Read `README.md:50-64` first, keep its voice, and extend it with (adapt
phrasing to fit the surrounding text, keep all existing model facts):

```markdown
**Live streaming (optional):** installing the *Nemotron Speech Streaming*
model from the Models tab (~720 MB, English only, NVIDIA Open Model License)
enables real local streaming — transcription runs while you speak and the text
is ready almost the moment you release the hotkey. It uses the MIT-licensed
[transcribe.cpp](https://github.com/handy-computer/transcribe.cpp) engine
(downloaded alongside the model, pinned and checksum-verified; see
THIRD-PARTY-NOTICES.md). Without it, local dictations are transcribed in one
pass after you stop — same results, just slower to appear. All local speech
audio stays on your machine either way. Two practical notes: the engine needs
the Microsoft Visual C++ x64 runtime (preinstalled on most PCs; if streaming
silently stays off, install it from aka.ms/vs/17/release/vc_redist.x64.exe),
and after your first streaming dictation the model stays loaded (~1 GB of
memory) until you close Winpepper.
```

- [ ] **Step 3: Run the Windows gate (the pre-push gate)**

First repeat the Task 9 Step 1 quiet-host poll, then:

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-streaming
timeout --foreground 2700 ./scripts/windows-gate.sh
```
Expected final line: `GATE: GREEN` (App build + 12 test project/TFM runs). If
it fails, fix and re-run; do not proceed with a red gate.

- [ ] **Step 4: Record the gate in the evidence doc**

Append to `docs/plans/2026-07-25-nemotron-streaming-evidence.md` under
"Windows pre-push gate result": the current commit sha (`git rev-parse HEAD`)
and the verbatim `GATE: GREEN` line (plus the gate's summary lines), following
the format of the 2026-07-25 streaming-verification evidence doc.

- [ ] **Step 5: Final Linux suite + commit**

```bash
./scripts/linux-tests.sh    # LINUX SUITE: GREEN
git add THIRD-PARTY-NOTICES.md README.md docs/plans/2026-07-25-nemotron-streaming-evidence.md
git commit -m "docs(licensing): THIRD-PARTY notices for transcribe.cpp runtime and nemotron weights; honest README streaming section; record GATE: GREEN

Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

Leave the branch unpushed and unmerged for user review.

---

## Spec-coverage map (Self-Review record)

| Spec deliverable | Covered by |
|---|---|
| 1. Native binding: contract.json gate before LoadLibrary, ABI struct-size verification (fail loud → batch fallback), explicit layouts, immediate string copies, PKST att-right, pin v0.1.3 | Tasks 1, 4 (gate order in `TranscribeCppEngine.Load`; fallback via holder returning null → batch adapter, and via `TranscribeCppException` → `NemotronStreamingTranscriber` fallback) |
| 2. Acquisition: GGUF + runtime tarball via ModelRegistry pattern (URL + SHA-256), extract + verify, storage under models dir, NOT in MSI, installable like other models | Tasks 2, 3, 7 (MSI untouched; hashes pinned & verified three ways) |
| 3. Streaming engine behind IStreamingTranscriber; mono-16k float chunks; committed growth; finalize at stop; R=13 default; CPU backend; StreamingDictationSession / fallback / OrphanedPumpGuard / archival preserved; TDT batch untouched; AppShell wiring gated on local+installed+enabled; blank-guard semantics (empty final ⇒ batch + loud warning) | Tasks 5, 6 (pipeline coordinator untouched; engine never disposed mid-run so no OrphanedPumpGuard interaction; archival lives in PipelineHost, untouched). Concurrency hardening from the load-bearing review: engine-wide compute gate (Task 4, v0.1.3 one-compute-per-model header contract) + session-level native lock with dispose-is-abort semantics (Task 5, verified pipeline behavior) |
| 4. UI: installable model card w/ honest English-only caption; StreamingEnabled caption update; default decision documented | Task 7; decision 1/2 in "Documented planner decisions"; default flip in Task 6 |
| 5. Verification: bench scenario, same binding+model, 4 WAV categories, streamed-vs-batch diff + post-stop latency, parity bar = streamed-nemotron == batch-nemotron, TDT comparison characterized, <500 ms for 10 s phrase, REAL numbers in evidence doc, end-to-end manual-equivalent check | Tasks 8, 9 (streamed leg runs the real production coordinator at real-time pacing = manual-equivalent) |
| 6. Docs/licensing: transcribe.cpp MIT notice, nemotron NVIDIA Open Model License recorded + surfaced, README updated honestly | Task 10 + Task 7 card caption |
| Env rules: Linux suite green per commit; windows-gate foreground with orphan poll; no system installs; no %LOCALAPPDATA%\winpepper writes; %TEMP% downloads; spike scratch reuse; commit style; nothing pushed | Global Constraints + every task's final step + Task 9 Step 1 / Task 10 Step 3 |

No silent deferrals: every fake/stub above (FakeTranscribeCppEngine, ProbeTranscriber, FakeRangeClient) is test/bench instrumentation; the production behavior each stands in for is proven by Task 9's real-model, real-engine, real-coordinator bench run with recorded transcripts and latencies, plus Task 10's windows-gate App build.

### Load-bearing assumption review (2026-07-25, post-planning)

The plan's assumptions were enumerated and validated (ledger:
`.worktrees/.the-usual-logs/nemotron-streaming/load-bearing-ledger.md` — 12
verified, 5 falsified-and-fixed, 5 accepted). Fixes folded in above:
engine-wide compute gate (v0.1.3 forbids concurrent compute per model),
session-level dispose-is-abort lock (the pipeline disposes sessions during
in-flight pushes), VC++ redist prerequisite surfaced (transcribe.dll statically
imports msvcp140/vcruntime140), extractor locked-tree ordering + restart
semantics, NVIDIA §3 notice text, memory-residency honesty (decision 5),
perf posture on weak hardware (decision 6), fallback-visibility asymmetry
(decision 7), and corrected line refs (`AppShell.cs:420`, bench arg loop
`:26-36`, `File.Move :173`, parakeet.h under `include/transcribe/`).
Independently RUN-verified during review (do not re-derive): the real
`ModelDownloader` downloads + resumes the pinned tarball from both hosts
anonymously; `TarFile.ExtractToDirectory` extracts the real v0.1.3 tarball to
the expected tree (17 plain file entries); HF serves the GGUF ungated.
Deliberately accepted (validated by Task 9 itself, honest-stop on failure):
streamed-vs-batch parity, CPU finalize latency, runtime committed-growth
timing — no primary spike output artifacts exist, so Task 9's bench is the
first recorded evidence; treat its acceptance criteria as real gates.
