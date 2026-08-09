using System.Text;

namespace Winpepper.Asr.TranscribeCpp.Worker;

/// <summary>
/// Wire opcodes for the transcribe.cpp worker subprocess. Requests are 1-7;
/// responses are 100+. One request always yields exactly one response.
///
/// Payload schemas (BinaryWriter/BinaryReader little-endian; strings are
/// int32 byte length (-1 = null) + UTF-8 bytes; floats are int32 count + raw
/// IEEE-754 bytes):
///   Load            = string runtimeDir, string ggufPath      -> LoadOk = string modelName
///   BeginStream     = int32 attContextRight, string? language -> BeginStreamOk = int32 gateWaitMs
///   Feed            = floats                                  -> FeedOk = string? committedText
///   FinalizeStream  = (empty)                                 -> FinalizeOk = string text, bool wasTruncated
///   DisposeStream   = (empty)                                 -> Ok (idempotent)
///   TranscribeBatch = string? language, floats                -> BatchOk = int32 gateWaitMs, string text
///   Shutdown        = (empty)                                 -> Ok, then the worker exits
///   Error           = int32 gateWaitMs, string exceptionTypeName, string message
/// </summary>
public enum WorkerOp : byte
{
    Load = 1,
    BeginStream = 2,
    Feed = 3,
    FinalizeStream = 4,
    DisposeStream = 5,
    TranscribeBatch = 6,
    Shutdown = 7,

    Ok = 100,
    LoadOk = 101,
    BeginStreamOk = 102,
    FeedOk = 103,
    FinalizeOk = 104,
    BatchOk = 105,
    Error = 110,
}

/// <summary>Length-prefixed binary framing: [byte op][int32 LE payloadLen][payload].</summary>
public static class WorkerWire
{
    /// <summary>Sanity cap: the largest legal payload is a full dictation's
    /// batch audio (minutes of 16 kHz float32); 64 MiB ≈ 17 minutes.</summary>
    public const int MaxPayloadBytes = 64 * 1024 * 1024;

    public static void WriteFrame(Stream s, WorkerOp op, byte[] payload)
    {
        // Guard the WRITE side too: an oversize frame is lethal to the peer
        // (its ReadFrame throws InvalidDataException and the process dies).
        // Failing here, before any bytes hit the stream, protects the peer
        // and leaves this connection usable.
        if (payload.Length > MaxPayloadBytes)
            throw new InvalidDataException(
                $"worker frame payload length {payload.Length} exceeds the {MaxPayloadBytes} cap; refusing to write a frame the peer would fatally reject");
        Span<byte> header = stackalloc byte[5];
        header[0] = (byte)op;
        BitConverter.TryWriteBytes(header[1..], payload.Length);
        s.Write(header);
        s.Write(payload, 0, payload.Length);
        s.Flush();
    }

    public static (WorkerOp Op, byte[] Payload) ReadFrame(Stream s)
    {
        Span<byte> header = stackalloc byte[5];
        ReadExactly(s, header);
        var op = (WorkerOp)header[0];
        var len = BitConverter.ToInt32(header[1..]);
        if (len < 0 || len > MaxPayloadBytes)
            throw new InvalidDataException($"worker frame payload length {len} is outside [0, {MaxPayloadBytes}]");
        var payload = new byte[len];
        if (len > 0) ReadExactly(s, payload);
        return (op, payload);
    }

    private static void ReadExactly(Stream s, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = s.Read(buffer[read..]);
            if (n == 0) throw new EndOfStreamException("worker stream closed mid-frame");
            read += n;
        }
    }

    public static void WriteString(BinaryWriter w, string? value)
    {
        if (value is null) { w.Write(-1); return; }
        var bytes = Encoding.UTF8.GetBytes(value);
        w.Write(bytes.Length);
        w.Write(bytes);
    }

    public static string? ReadString(BinaryReader r)
    {
        var len = r.ReadInt32();
        if (len == -1) return null;
        if (len < 0 || len > MaxPayloadBytes) throw new InvalidDataException($"string length {len} out of range");
        return Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    public static void WriteFloats(BinaryWriter w, float[] samples, int count)
    {
        w.Write(count);
        for (var i = 0; i < count; i++) w.Write(samples[i]);
    }

    public static float[] ReadFloats(BinaryReader r)
    {
        var count = r.ReadInt32();
        if (count < 0 || count > MaxPayloadBytes / sizeof(float))
            throw new InvalidDataException($"float count {count} out of range");
        var samples = new float[count];
        for (var i = 0; i < count; i++) samples[i] = r.ReadSingle();
        return samples;
    }
}
