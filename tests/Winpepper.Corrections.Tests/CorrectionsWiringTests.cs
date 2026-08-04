using Shouldly;
using Winpepper.Core.ViewModels;
using Winpepper.Corrections;
using Xunit;

namespace Winpepper.Corrections.Tests;

public class CorrectionsWiringTests : IDisposable
{
    private readonly string _path;

    public CorrectionsWiringTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"corrections-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), $"{Path.GetFileName(_path)}.tmp-*"))
            File.Delete(f);
    }

    [Fact]
    public void Vm_Add_RoundTrips_To_Disk()
    {
        var vm = CorrectionsWiring.CreateViewModel(new CorrectionStore(_path));

        vm.AddPreferred("ChatGPT").ShouldBeNull();
        vm.AddReplacement("chat gbt", "ChatGPT").ShouldBeNull();

        // Persistence is proven by a FRESH store over the same path, exactly
        // like the dictation pipeline reads it — not by in-memory state.
        var loaded = new CorrectionStore(_path).Load();
        loaded.Preferred.ShouldBe(new[] { "ChatGPT" });
        loaded.Replacements["chat gbt"].ShouldBe("ChatGPT");
    }

    [Fact]
    public void Vm_Seeds_From_Existing_Store()
    {
        new CorrectionStore(_path).Save(new CorrectionsData
        {
            Preferred = new[] { "ChatGPT", "Anthropic" },
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat gbt"] = "ChatGPT",
            },
        });

        var vm = CorrectionsWiring.CreateViewModel(new CorrectionStore(_path));

        vm.Preferred.Select(p => p.Text).ShouldBe(new[] { "ChatGPT", "Anthropic" });
        vm.Replacements.Count.ShouldBe(1);
        vm.Replacements[0].Wrong.ShouldBe("chat gbt");
        vm.Replacements[0].Right.ShouldBe("ChatGPT");
    }

    [Fact]
    public void Vm_Remove_Persists_The_Removal()
    {
        new CorrectionStore(_path).Save(new CorrectionsData
        {
            Preferred = new[] { "ChatGPT", "Anthropic" },
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat gbt"] = "ChatGPT",
            },
        });
        var vm = CorrectionsWiring.CreateViewModel(new CorrectionStore(_path));

        vm.RemovePreferred(vm.Preferred.Single(p => p.Text == "ChatGPT"));
        vm.RemoveReplacement(vm.Replacements[0]);

        var loaded = new CorrectionStore(_path).Load();
        loaded.Preferred.ShouldBe(new[] { "Anthropic" });
        loaded.Replacements.ShouldBeEmpty();
    }

    [Fact]
    public void Persist_Failure_Does_Not_Throw_Out_Of_Add_Or_Remove()
    {
        // The store path's PARENT is a regular file, so AtomicFile's
        // Directory.CreateDirectory throws IOException on every Save —
        // deterministic on both Linux and Windows.
        var blocker = Path.Combine(Path.GetTempPath(), $"corrections-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "");
        try
        {
            var store = new CorrectionStore(Path.Combine(blocker, "corrections.json"));
            Exception? seen = null;
            var vm = CorrectionsWiring.CreateViewModel(store, onError: ex => seen = ex);

            Should.NotThrow(() => vm.AddPreferred("ChatGPT"));
            seen.ShouldNotBeNull();
            vm.Preferred.Count.ShouldBe(1); // in-memory edit is kept

            seen = null;
            Should.NotThrow(() => vm.RemovePreferred(vm.Preferred[0]));
            seen.ShouldNotBeNull();
            vm.Preferred.ShouldBeEmpty();
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void Persist_Failure_Without_OnError_Is_Still_Contained()
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"corrections-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "");
        try
        {
            var store = new CorrectionStore(Path.Combine(blocker, "corrections.json"));
            var vm = CorrectionsWiring.CreateViewModel(store);

            Should.NotThrow(() => vm.AddPreferred("ChatGPT"));
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void Seed_Load_Failure_Falls_Back_To_Empty_And_Reports()
    {
        File.WriteAllText(_path, """{"schema":1,"preferred":["ChatGPT"],"replacements":{}}""");
        // Hold the file with FileShare.None: CorrectionStore.Load()'s
        // File.ReadAllText then throws IOException (native sharing on
        // Windows; flock-based FileShare emulation between FileStreams on
        // Linux). Load() only swallows JsonException, so this escapes it.
        using var locker = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);

        Exception? seen = null;
        CorrectionsViewModel vm = null!;
        Should.NotThrow(() =>
            vm = CorrectionsWiring.CreateViewModel(new CorrectionStore(_path), onError: ex => seen = ex));

        vm.Preferred.ShouldBeEmpty();
        vm.Replacements.ShouldBeEmpty();
        seen.ShouldNotBeNull();
    }

    [Fact]
    public void Failed_Seed_Disables_Persistence_And_Never_Wipes_The_File()
    {
        File.WriteAllText(_path, """{"schema":1,"preferred":["ChatGPT"],"replacements":{}}""");
        Exception? seen = null;
        CorrectionsViewModel vm = null!;
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            vm = CorrectionsWiring.CreateViewModel(new CorrectionStore(_path), onError: ex => seen = ex);
        }

        // The lock is gone, so a Save here WOULD succeed — but the disk
        // still holds data this VM never saw. The factory must refuse:
        // a degraded load can never become the base of a full-file
        // rewrite (docs/plans/2026-07-26-settings-lost-update.md).
        seen = null;
        Should.NotThrow(() => vm.AddPreferred("Anthropic"));
        seen.ShouldNotBeNull();                                            // refusal is reported
        vm.Preferred.Select(p => p.Text).ShouldBe(new[] { "Anthropic" }); // in-memory edit kept

        var loaded = new CorrectionStore(_path).Load();
        loaded.Preferred.ShouldBe(new[] { "ChatGPT" });                   // file untouched
    }

    [Fact]
    public void Replacements_Display_NewestFirst_While_Disk_Stays_OldestFirst()
    {
        // Seed the store the way an existing user's corrections.json looks:
        // oldest-first, append-at-end.
        new CorrectionStore(_path).Save(new CorrectionsData
        {
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat gbt"] = "ChatGPT",
                ["ann thropic"] = "Anthropic",
            },
        });

        var vm = CorrectionsWiring.CreateViewModel(new CorrectionStore(_path));

        // Display order: newest (last on disk) first.
        vm.Replacements.Select(r => r.Wrong)
            .ShouldBe(new[] { "ann thropic", "chat gbt" });

        // A UI add lands at the TOP of the display...
        vm.AddReplacement("open ai", "OpenAI").ShouldBeNull();
        vm.Replacements.Select(r => r.Wrong)
            .ShouldBe(new[] { "open ai", "ann thropic", "chat gbt" });

        // ...but is APPENDED at the END of the persisted file, keeping the
        // disk contract identical to today's and to the post-paste learning
        // writer. Proven with a FRESH store over the same path, exactly like
        // the dictation pipeline reads it.
        var loaded = new CorrectionStore(_path).Load();
        loaded.Replacements.Keys.ShouldBe(new[] { "chat gbt", "ann thropic", "open ai" });

        // And a fresh VM seeded from that file renders newest-first again —
        // the "relaunch shows newest-first" guarantee.
        var reseeded = CorrectionsWiring.CreateViewModel(new CorrectionStore(_path));
        reseeded.Replacements.Select(r => r.Wrong)
            .ShouldBe(new[] { "open ai", "ann thropic", "chat gbt" });
    }
}
