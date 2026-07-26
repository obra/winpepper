using System.Globalization;
using AsrEvalCorpus;
using AsrLatencyBench;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Winpepper.Asr.Transcription;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0];
string? historyDir = null;
string? corpusDir = null;
int? take = null;
var force = false;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--history": historyDir = args[++i]; break;
        case "--corpus": corpusDir = args[++i]; break;
        case "--take": take = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--force": force = true; break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            PrintUsage();
            return 1;
    }
}
if (corpusDir is null)
{
    Console.Error.WriteLine("--corpus <dir> is required");
    PrintUsage();
    return 1;
}

switch (command)
{
    case "export":
        if (historyDir is null)
        {
            Console.Error.WriteLine("export requires --history <dir>");
            return 1;
        }
        return RunExport(historyDir, corpusDir, take);
    case "references":
        return await RunReferences(corpusDir, force);
    default:
        Console.Error.WriteLine($"unknown command: {command}");
        PrintUsage();
        return 1;
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        usage:
          AsrEvalCorpus export --history <app-history-dir> --corpus <corpus-dir> [--take N]
          AsrEvalCorpus references --corpus <corpus-dir> [--force]

        export      copies new dictation clips out of the app's rolling history into a
                    durable corpus folder (read-only on the history side; re-runnable,
                    never duplicates a clip).
        references  generates a reference transcript per clip via AssemblyAI
                    (needs ASSEMBLYAI_API_KEY; skips clips that already have one).
        """);
}

static int RunExport(string historyDir, string corpusDir, int? take)
{
    var indexPath = Path.Combine(historyDir, "index.json");
    if (!File.Exists(indexPath))
    {
        Console.Error.WriteLine($"export: no history index at {indexPath}");
        return 1;
    }
    var history = HistoryIndex.Load(indexPath);
    var manifestPath = Path.Combine(corpusDir, "manifest.json");
    var manifest = CorpusManifest.LoadOrEmpty(manifestPath);
    var plan = CorpusExport.BuildPlan(history.Entries, manifest, take);

    Directory.CreateDirectory(Path.Combine(corpusDir, "clips"));
    var copied = 0;
    var missing = 0;
    foreach (var item in plan.ToAdd)
    {
        var src = Path.Combine(historyDir, item.Source.WavRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(src))
        {
            missing++;
            Console.Error.WriteLine($"export[{item.Entry.Id}]: WAV missing in history (already pruned?), skipped");
            continue;
        }
        File.Copy(src, Path.Combine(corpusDir, item.Entry.WavPath), overwrite: true);
        manifest.Entries.Add(item.Entry);
        copied++;
    }
    manifest.Save(manifestPath);
    Console.WriteLine($"export: {copied} new clips copied, {plan.SkippedExisting} already in corpus, {missing} missing WAVs");
    return 0;
}

static async Task<int> RunReferences(string corpusDir, bool force)
{
    var key = Environment.GetEnvironmentVariable("ASSEMBLYAI_API_KEY");
    if (string.IsNullOrWhiteSpace(key))
    {
        Console.Error.WriteLine(
            "references: ASSEMBLYAI_API_KEY is not set. Set it and re-run -- reference transcripts are never fabricated.");
        return 1;
    }
    var manifestPath = Path.Combine(corpusDir, "manifest.json");
    if (!File.Exists(manifestPath))
    {
        Console.Error.WriteLine($"references: no manifest at {manifestPath} (run export first)");
        return 1;
    }
    var manifest = CorpusManifest.Load(manifestPath);

    var opts = new AssemblyAiOptions
    {
        Model = AssemblyAiModels.DefaultId,        // "universal-3-5-pro" -- the options default is universal-2!
        Disfluencies = true,                       // KEEP "um"/"uh": local models transcribe fillers verbatim
        CloudDeadline = TimeSpan.FromSeconds(120), // dictation clips, but allow slow batch turnaround
        PollInterval = TimeSpan.FromSeconds(2),
        DeleteAfterTranscribe = true,              // do not leave transcripts on AssemblyAI's servers
    };
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    var client = new AssemblyAiClient(http, () => key, opts, NullLogger<AssemblyAiClient>.Instance);
    // DeleteAfterTranscribe deletes are DETACHED in the transcriber: its default
    // scheduleDetached is `a => _ = Task.Run(a)` (AssemblyAiTranscriber.cs:38), which
    // would race process exit in this short-lived CLI. Capture the delete task via the
    // injectable scheduleDetached hook (ctor param, AssemblyAiTranscriber.cs:30) and
    // await it per clip below, so every remote transcript delete deterministically
    // completes before exit. The transcriber gets a console warning logger so a failed
    // delete (logged inside ScheduleDelete, AssemblyAiTranscriber.cs:94) is visible.
    Task? pendingDelete = null;
    var transcriber = new AssemblyAiTranscriber(
        client, new EnvKeyStore(), opts, new ConsoleWarnLogger<AssemblyAiTranscriber>(),
        scheduleDetached: work => pendingDelete = work());

    var written = 0;
    var skipped = 0;
    var failed = 0;
    foreach (var entry in manifest.Entries)
    {
        var refPath = ReferencePlanner.ReferencePath(corpusDir, entry);
        switch (ReferencePlanner.Decide(entry, File.Exists(refPath), force))
        {
            case ReferenceAction.Skip:
                skipped++;
                break;
            case ReferenceAction.WriteEmpty:
                File.WriteAllText(refPath, "");
                written++;
                Console.WriteLine($"references[{entry.Id}]: expected-silent -> empty reference");
                break;
            case ReferenceAction.Transcribe:
                try
                {
                    var audio = BenchAudio.ReadMono16k(Path.Combine(corpusDir, entry.WavPath));
                    var result = await transcriber.TranscribeAsync(audio, CancellationToken.None);
                    File.WriteAllText(refPath, result.Text.TrimEnd() + Environment.NewLine);
                    written++;
                    Console.WriteLine($"references[{entry.Id}]: ok ({result.Text.Length} chars)");
                    if (pendingDelete is not null)
                    {
                        // Drain the remote-transcript delete NOW, per clip: ScheduleDelete's
                        // own try/catch means this never throws; a failed delete surfaces as
                        // a [Warning] line from ConsoleWarnLogger, not an exception.
                        await pendingDelete;
                        pendingDelete = null;
                        Console.WriteLine($"references[{entry.Id}]: remote transcript delete drained");
                    }
                }
                catch (Exception ex)
                {
                    // Covers transport failures AND transcripts that complete with
                    // status "error" -- AssemblyAiTranscriber.cs:73-74 throws
                    // AssemblyAiException for those, e.g. "Audio duration is too
                    // short." (documented minimum 160 ms; the 0.51 s corpus floor
                    // clears it, but handle it anyway). The loop CONTINUES to the
                    // next clip; no reference file is written for this one.
                    failed++;
                    Console.Error.WriteLine($"references[{entry.Id}]: FAILED {ex.Message}");
                }
                break;
        }
    }
    Console.WriteLine($"references: {written} written, {skipped} skipped, {failed} failed");
    // Non-zero exit when any clip failed. Re-running retries ONLY missing/failed
    // references: Decide skips clips whose reference file already exists, and a
    // failed clip never wrote one -- idempotent by construction.
    return failed == 0 ? 0 : 1;
}

/// <summary>Presence gate only; the real key goes to AssemblyAiClient via the () => key closure.</summary>
sealed class EnvKeyStore : Winpepper.Asr.Transcription.IAssemblyAiKeyStore
{
    public bool HasKey => true;
    public void Save(string apiKey) { }
    public string? Load() => null;
    public void Clear() { }
}

/// <summary>Warning+ console logger so the transcriber's non-fatal warnings
/// (notably a failed remote transcript delete) are visible in this CLI instead
/// of being swallowed by NullLogger. Never logs the API key (the transcriber
/// already guarantees that).</summary>
sealed class ConsoleWarnLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
            Console.Error.WriteLine($"[{logLevel}] {formatter(state, exception)}");
    }
}
