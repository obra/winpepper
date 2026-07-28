// Cleanup-LLM latency benchmark. Measures the wall time of the REAL production
// cleanup path (CleanupRunner + LlamaCleanupBackend) over real dictation
// statements exported from the app's history, plus (optionally) the committed
// eval case texts. Mirrors scripts/asr-latency-bench.
//
// Scenarios:
//   export-statements --history-dir <dir> --out <file.jsonl> [--max N]
//     READ-ONLY parse of <dir>/index.json, newest first, into statements JSONL.
//     No length filtering: the bench must observe production behavior including
//     the <4-word bypass.
//   latency --statements <file.jsonl> [--include-eval-cases]
//           --models-root <dir> [--model <registry-key>] [--passes N=3]
//           [--out <dir>=artifacts/cleanup-bench-results] [--seed 42]
//           [--timeout-ms 15000]
//     Loads the resolved cleanup GGUF once (load and warm timed separately,
//     never in per-statement samples), then runs every statement through
//     CleanupRunner.RunAsync per pass. Per-statement failures become error
//     rows; results.json (full text) + results.md (numbers/ids only) are
//     ALWAYS written; exit 1 when any statement errored, exit 2 on usage or
//     configuration errors.
using System.Diagnostics;
using CleanupLatencyBench;
using Microsoft.Extensions.Logging.Abstractions;
using Winpepper.Cleanup;
using Winpepper.Corrections;
using Winpepper.Models;

if (args.Length == 0)
{
    PrintUsage();
    Environment.ExitCode = 2;
    return;
}

// Environment.ExitCode + plain return (NOT `return <int>`), same as the ASR
// bench: an int-returning top-level Main would override Environment.ExitCode
// with the implicit 0 from falling off the end.
var scenario = args[0];
var rest = args.Skip(1).ToArray();
switch (scenario)
{
    case "export-statements":
    {
        var a = CleanupBenchArgs.ParseExport(rest);
        if (a.Error is not null) { Console.Error.WriteLine(a.Error); Environment.ExitCode = 2; return; }

        // READ-ONLY: only ever File.ReadAllText on the history index.
        var indexPath = Path.Combine(a.HistoryDir!, "index.json");
        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine($"export-statements: no index.json in {a.HistoryDir}");
            Environment.ExitCode = 2;
            return;
        }
        IReadOnlyList<BenchStatement> statements;
        try
        {
            statements = CleanupBenchStatements.ParseHistoryIndex(File.ReadAllText(indexPath));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"export-statements: failed to parse {indexPath}: {ex.Message}");
            Environment.ExitCode = 2;
            return;
        }
        if (a.Max > 0) statements = statements.Take(a.Max).ToList();

        var outDirName = Path.GetDirectoryName(Path.GetFullPath(a.OutFile!));
        if (!string.IsNullOrEmpty(outDirName)) Directory.CreateDirectory(outDirName);
        File.WriteAllText(a.OutFile!, CleanupBenchStatements.ToJsonl(statements));
        Console.WriteLine($"export-statements: wrote {statements.Count} statement(s) to {a.OutFile}");
        break;
    }
    case "latency":
    {
        var a = CleanupBenchArgs.ParseLatency(rest);
        if (a.Error is not null) { Console.Error.WriteLine(a.Error); Environment.ExitCode = 2; return; }

        if (!File.Exists(a.StatementsFile))
        {
            Console.Error.WriteLine($"latency: statements file not found: {a.StatementsFile}");
            Environment.ExitCode = 2;
            return;
        }
        List<BenchStatement> statements;
        try
        {
            statements = CleanupBenchStatements.ParseJsonl(File.ReadAllText(a.StatementsFile!)).ToList();
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"latency: {ex.Message}");
            Environment.ExitCode = 2;
            return;
        }
        if (a.IncludeEvalCases)
        {
            statements.AddRange(CleanupEvalStatements.All
                .Select(s => new BenchStatement(s.Name, s.RawTranscript)));
        }
        if (statements.Count == 0)
        {
            Console.Error.WriteLine("latency: no statements to run (empty JSONL and no --include-eval-cases)");
            Environment.ExitCode = 2;
            return;
        }

        // Resolve the GGUF via the production resolver: --model omitted =
        // registry cleanup default. An unknown --model silently falls back
        // inside ResolveOrDefault; surface that as a hard error instead of
        // benching the wrong model.
        var resolution = CleanupModelPathResolver.Resolve(new ModelRegistry(), a.ModelsRoot!, a.Model);
        if (a.Model is not null && resolution.FellBackToDefault)
        {
            Console.Error.WriteLine(
                $"latency: unknown cleanup model '{a.Model}' (registry default is '{resolution.ResolvedName}'); " +
                $"pass a ModelKind.Cleanup registry key or omit --model for the default");
            Environment.ExitCode = 2;
            return;
        }
        if (resolution.GgufPath is null)
        {
            Console.Error.WriteLine($"latency: cleanup model '{resolution.ResolvedName}' declares no .gguf file in the registry");
            Environment.ExitCode = 2;
            return;
        }
        if (!File.Exists(resolution.GgufPath))
        {
            Console.Error.WriteLine(
                $"latency: model file missing: {resolution.GgufPath} -- install it via the app's Models page " +
                $"or point --models-root at a models root that contains it");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine($"# latency: model={resolution.ResolvedName} promptFormat={resolution.PromptFormat} " +
            $"omitPromptExample={resolution.OmitPromptExample} gpuLayers={a.GpuLayers} " +
            $"statements={statements.Count} passes={a.Passes} seed={a.Seed} timeoutMs={a.TimeoutMs}");
        Console.WriteLine($"# latency: gguf={resolution.GgufPath}");

        // Model load and warm are timed once, SEPARATELY, and recorded in the
        // run info -- never in per-statement samples.
        var swLoad = Stopwatch.StartNew();
        using var backend = new LlamaCleanupBackend(
            resolution.GgufPath,
            a.Verbose ? new StderrLogger<LlamaCleanupBackend>() : NullLogger<LlamaCleanupBackend>.Instance,
            gpuLayerCount: a.GpuLayers,
            samplingSeed: (uint)a.Seed,
            promptFormat: resolution.PromptFormat);
        swLoad.Stop();
        var swWarm = Stopwatch.StartNew();
        await backend.WarmAsync(CancellationToken.None);
        swWarm.Stop();
        Console.WriteLine($"# latency: model load {swLoad.ElapsedMilliseconds} ms, warm {swWarm.ElapsedMilliseconds} ms");

        var runner = new CleanupRunner(backend,
            a.Verbose ? new StderrLogger<CleanupRunner>() : NullLogger<CleanupRunner>.Instance,
            omitPromptExample: resolution.OmitPromptExample);
        var options = new CleanupOptions { Timeout = TimeSpan.FromMilliseconds(a.TimeoutMs) };

        var tallies = statements.Select(s => new StatementTally(s)).ToList();
        for (var pass = 1; pass <= a.Passes; pass++)
        {
            Console.WriteLine($"# latency: pass {pass}/{a.Passes}");
            foreach (var tally in tallies)
            {
                // One bad statement must not destroy the whole run: a per-
                // statement failure becomes an error row (retired for later
                // passes) and results are still written after the loop.
                if (tally.Error is not null) continue;
                try
                {
                    var sw = Stopwatch.StartNew();
                    var result = await runner.RunAsync(
                        tally.Statement.Text, CorrectionsData.Empty,
                        windowContextTask: null, options, CancellationToken.None);
                    sw.Stop();
                    tally.CallMs.Add(sw.ElapsedMilliseconds);
                    tally.ElapsedMs.Add((long)result.Elapsed.TotalMilliseconds);
                    tally.Paths.Add(result.Path.ToString());
                    tally.Outputs.Add(result.CleanedText);
                    tally.RawModelOutputs.Add(result.RawModelOutput);
                }
                catch (Exception ex)
                {
                    tally.Error = $"{ex.GetType().Name}: {ex.Message}";
                    Console.Error.WriteLine($"latency[{tally.Statement.Id}] ERROR: {tally.Error}");
                }
            }
        }

        var results = tallies.Select(t => new StatementResult(
            t.Statement.Id,
            t.Statement.Text.Length,
            CleanupBenchResults.WordCount(t.Statement.Text),
            t.Statement.Text,
            t.CallMs, t.ElapsedMs, t.Paths, t.Outputs,
            t.RawModelOutputs,
            t.Error)).ToList();

        var info = new BenchRunInfo(
            Model: resolution.ResolvedName,
            PromptFormat: resolution.PromptFormat,
            DateUtc: DateTime.UtcNow.ToString("yyyy-MM-dd"),
            Passes: a.Passes,
            Seed: a.Seed,
            TimeoutMs: a.TimeoutMs,
            ModelLoadMs: swLoad.ElapsedMilliseconds,
            WarmMs: swWarm.ElapsedMilliseconds,
            StatementsSource: Path.GetFileName(a.StatementsFile!)
                + (a.IncludeEvalCases ? $" + {CleanupEvalStatements.All.Count} eval cases" : ""));
        var summary = CleanupBenchResults.Summarize(results);

        // ALWAYS write both files -- even when statements failed -- then report.
        Directory.CreateDirectory(a.OutDir);
        File.WriteAllText(Path.Combine(a.OutDir, "results.json"),
            CleanupBenchResults.ToJson(info, results, summary));
        var resultsMd = CleanupBenchResults.ToMarkdown(info, results, summary);
        File.WriteAllText(Path.Combine(a.OutDir, "results.md"), resultsMd);
        Console.WriteLine();
        Console.WriteLine(resultsMd);
        if (summary.FailedCount > 0)
        {
            // Results are already on disk; the non-zero exit only flags the failures.
            Console.Error.WriteLine($"latency: {summary.FailedCount} statement(s) FAILED");
            Environment.ExitCode = 1;
        }
        break;
    }
    default:
        Console.Error.WriteLine($"unknown scenario '{scenario}'");
        PrintUsage();
        Environment.ExitCode = 2;
        break;
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        usage:
          CleanupLatencyBench export-statements --history-dir <dir> --out <file.jsonl> [--max N]
          CleanupLatencyBench latency --statements <file.jsonl> [--include-eval-cases]
                              --models-root <dir> [--model <registry-key>] [--passes N]
                              [--out <dir>] [--seed N] [--timeout-ms N] [--verbose]
                              [--gpu-layers N]  (0 = CPU inference; default 999 = full GPU offload)
        """);
}

/// <summary>Per-statement accumulator for the multi-pass loop. A non-null
/// Error permanently retires the statement (mirrors the ASR bench's ClipTally).</summary>
sealed class StatementTally
{
    public StatementTally(BenchStatement statement) => Statement = statement;
    public BenchStatement Statement { get; }
    public List<long> CallMs { get; } = new();
    public List<long> ElapsedMs { get; } = new();
    public List<string> Paths { get; } = new();
    public List<string> Outputs { get; } = new();
    public List<string> RawModelOutputs { get; } = new();
    public string? Error { get; set; }
}

/// <summary>Minimal console logger for --verbose: surfaces CleanupRunner's
/// guard-rejection warnings (with output previews) and backend logs on stderr
/// without pulling in a console-logging package. Results stay on stdout.</summary>
sealed class StderrLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) =>
        logLevel >= Microsoft.Extensions.Logging.LogLevel.Debug;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        Console.Error.WriteLine($"[{logLevel}] {typeof(T).Name}: {formatter(state, exception)}"
            + (exception is null ? "" : $" :: {exception}"));
    }
}
