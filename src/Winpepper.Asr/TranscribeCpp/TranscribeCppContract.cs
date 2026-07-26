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
