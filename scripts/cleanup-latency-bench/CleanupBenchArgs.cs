using System;
using System.Collections.Generic;
using System.Globalization;

namespace CleanupLatencyBench;

/// <summary>Parsed <c>export-statements</c> arguments. A non-null
/// <see cref="Error"/> means the run must be rejected (usage error, exit 2)
/// and the other fields are unreliable.</summary>
public sealed record ExportStatementsArgs(
    string? HistoryDir,
    string? OutFile,
    int Max,
    string? Error);

/// <summary>Parsed <c>latency</c> arguments. A non-null <see cref="Error"/>
/// means the run must be rejected (usage error, exit 2).</summary>
public sealed record LatencyArgs(
    string? StatementsFile,
    bool IncludeEvalCases,
    string? ModelsRoot,
    string? Model,
    int Passes,
    string OutDir,
    int Seed,
    int TimeoutMs,
    bool Verbose,
    int GpuLayers,
    string? Error);

/// <summary>
/// Bench argument parsing for the two scenarios. BCL-only so the same file
/// compiles into Winpepper.Cleanup.Tests (same linked-file pattern as the ASR
/// bench's BenchArgs.cs).
/// </summary>
public static class CleanupBenchArgs
{
    public const int DefaultPasses = 3;
    public const int DefaultSeed = 42;
    public const int DefaultTimeoutMs = 15_000;

    // Matches LlamaCleanupBackend's default (offload everything to the GPU).
    // --gpu-layers 0 forces CPU inference through the SAME production backend,
    // isolating GPU/Vulkan numeric issues from template/model issues.
    public const int DefaultGpuLayers = 999;

    // Default INSIDE gitignored artifacts/: results.json contains transcript
    // text, and a bare "cleanup-bench-results/" would NOT be gitignored.
    public const string DefaultOutDir = "artifacts/cleanup-bench-results";

    public static ExportStatementsArgs ParseExport(IReadOnlyList<string> args)
    {
        string? historyDir = null;
        string? outFile = null;
        var max = 0;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--history-dir":
                    if (!TryTakeValue(args, ref i, out var hd)) return ExportError("--history-dir requires a value");
                    historyDir = hd;
                    break;
                case "--out":
                    if (!TryTakeValue(args, ref i, out var of)) return ExportError("--out requires a value");
                    outFile = of;
                    break;
                case "--max":
                    if (!TryTakeValue(args, ref i, out var mv)) return ExportError("--max requires a value");
                    if (!TryParseInt(mv, out max)) return ExportError($"--max must be an integer, got '{mv}'");
                    break;
                default:
                    return ExportError($"unknown export-statements argument '{args[i]}'");
            }
        }

        if (historyDir is null) return ExportError("--history-dir is required");
        if (outFile is null) return ExportError("--out is required");
        if (max < 0) return ExportError($"--max must be >= 0 (0 = all entries), got {max}");
        return new ExportStatementsArgs(historyDir, outFile, max, Error: null);

        static ExportStatementsArgs ExportError(string message) =>
            new(null, null, 0, Error: message);
    }

    public static LatencyArgs ParseLatency(IReadOnlyList<string> args)
    {
        string? statementsFile = null;
        var includeEvalCases = false;
        string? modelsRoot = null;
        string? model = null;
        var passes = DefaultPasses;
        var outDir = DefaultOutDir;
        var seed = DefaultSeed;
        var timeoutMs = DefaultTimeoutMs;
        var verbose = false;
        var gpuLayers = DefaultGpuLayers;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--statements":
                    if (!TryTakeValue(args, ref i, out var sf)) return LatencyError("--statements requires a value");
                    statementsFile = sf;
                    break;
                case "--include-eval-cases":
                    includeEvalCases = true;
                    break;
                case "--models-root":
                    if (!TryTakeValue(args, ref i, out var mr)) return LatencyError("--models-root requires a value");
                    modelsRoot = mr;
                    break;
                case "--model":
                    if (!TryTakeValue(args, ref i, out var m)) return LatencyError("--model requires a value");
                    model = m;
                    break;
                case "--passes":
                    if (!TryTakeValue(args, ref i, out var pv)) return LatencyError("--passes requires a value");
                    if (!TryParseInt(pv, out passes)) return LatencyError($"--passes must be an integer, got '{pv}'");
                    break;
                case "--out":
                    if (!TryTakeValue(args, ref i, out var od)) return LatencyError("--out requires a value");
                    outDir = od;
                    break;
                case "--seed":
                    if (!TryTakeValue(args, ref i, out var sv)) return LatencyError("--seed requires a value");
                    if (!TryParseInt(sv, out seed)) return LatencyError($"--seed must be an integer, got '{sv}'");
                    break;
                case "--timeout-ms":
                    if (!TryTakeValue(args, ref i, out var tv)) return LatencyError("--timeout-ms requires a value");
                    if (!TryParseInt(tv, out timeoutMs)) return LatencyError($"--timeout-ms must be an integer, got '{tv}'");
                    break;
                case "--verbose":
                    // Wires a real console logger into the backend/runner so
                    // CleanupRunner's guard-rejection warnings (with output
                    // previews) become visible during debugging runs.
                    verbose = true;
                    break;
                case "--gpu-layers":
                    if (!TryTakeValue(args, ref i, out var gl)) return LatencyError("--gpu-layers requires a value");
                    if (!TryParseInt(gl, out gpuLayers)) return LatencyError($"--gpu-layers must be an integer, got '{gl}'");
                    break;
                default:
                    return LatencyError($"unknown latency argument '{args[i]}'");
            }
        }

        if (statementsFile is null) return LatencyError("--statements is required");
        if (modelsRoot is null) return LatencyError("--models-root is required");
        if (passes < 1) return LatencyError($"--passes must be >= 1, got {passes}");
        // The sampling seed is forwarded to LlamaCleanupBackend as a uint.
        if (seed < 0) return LatencyError($"--seed must be >= 0, got {seed}");
        if (timeoutMs < 1) return LatencyError($"--timeout-ms must be >= 1, got {timeoutMs}");
        if (gpuLayers < 0) return LatencyError($"--gpu-layers must be >= 0, got {gpuLayers}");
        return new LatencyArgs(statementsFile, includeEvalCases, modelsRoot, model,
            passes, outDir, seed, timeoutMs, verbose, gpuLayers, Error: null);

        static LatencyArgs LatencyError(string message) =>
            new(null, false, null, null, DefaultPasses, DefaultOutDir, DefaultSeed,
                DefaultTimeoutMs, Verbose: false, GpuLayers: DefaultGpuLayers, Error: message);
    }

    private static bool TryTakeValue(IReadOnlyList<string> args, ref int i, out string value)
    {
        if (i + 1 >= args.Count)
        {
            value = string.Empty;
            return false;
        }
        value = args[++i];
        return true;
    }

    private static bool TryParseInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
