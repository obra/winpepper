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
