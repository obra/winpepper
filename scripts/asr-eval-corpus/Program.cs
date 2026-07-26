using System.Globalization;
using AsrEvalCorpus;

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
_ = force; // used by the references command (Task 6)

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
