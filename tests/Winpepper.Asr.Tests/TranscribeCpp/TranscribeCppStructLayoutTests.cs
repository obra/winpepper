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

    [Fact]
    public void RunParams_layout_matches_transcribe_h_v013()
    {
        Assert.Equal(64, Marshal.SizeOf<TranscribeCppNative.RunParams>());
        Assert.Equal(0, (int)Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.struct_size)));
        Assert.Equal(8, (int)Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.task)));
        Assert.Equal(12, (int)Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.timestamps)));
        Assert.Equal(16, (int)Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.pnc)));
        Assert.Equal(20, (int)Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.itn)));
        Assert.Equal(24, (int)Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.language)));
        Assert.Equal(32, (int)Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.target_language)));
        Assert.Equal(40, (int)Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.keep_special_tags)));
        Assert.Equal(48, (int)Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.family)));
        Assert.Equal(56, (int)Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.spec_k_drafts)));
        Assert.Equal(2, TranscribeCppNative.ABI_RUN_PARAMS);
    }
}
