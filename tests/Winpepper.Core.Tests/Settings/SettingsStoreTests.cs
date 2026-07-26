using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests.Settings;

public class SettingsStoreTests : IDisposable
{
    private readonly string _path;
    public SettingsStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
    }
    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        var dir = Path.GetDirectoryName(_path)!;
        foreach (var f in Directory.GetFiles(dir, $"{Path.GetFileName(_path)}.bad-*"))
            File.Delete(f);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var store = new SettingsStore(_path);
        var s = store.Load();
        s.Schema.ShouldBe(1);
        s.MicDeviceId.ShouldBe("");
        s.AsrModelName.ShouldBe("parakeet-tdt-0.6b-v3");
        s.PlaySounds.ShouldBeTrue();
    }

    [Fact]
    public void Save_Then_Load_RoundTrips()
    {
        var store = new SettingsStore(_path);
        var s = store.Load();
        s = s with { MicDeviceId = "{abc-123}", PlaySounds = false };
        store.Save(s);
        var loaded = new SettingsStore(_path).Load();
        loaded.MicDeviceId.ShouldBe("{abc-123}");
        loaded.PlaySounds.ShouldBeFalse();
    }

    [Fact]
    public void Load_BadJson_FallsBackToDefaults()
    {
        File.WriteAllText(_path, "{ not json");
        var s = new SettingsStore(_path).Load();
        s.Schema.ShouldBe(1);
    }

    [Fact]
    public void Save_Uses_AtomicWrite()
    {
        var store = new SettingsStore(_path);
        store.Save(store.Load());
        Path.GetDirectoryName(_path)!
            .Pipe(d => Directory.GetFiles(d, $"{Path.GetFileName(_path)}.tmp-*"))
            .Length.ShouldBe(0);
    }

    [Fact]
    public void Defaults_Include_NewPlan3Fields()
    {
        var s = new SettingsStore(_path).Load();
        s.AutostartEnabled.ShouldBeFalse();
        s.OnboardingCompleted.ShouldBeFalse();
        s.SpeakerFilterEnabled.ShouldBeFalse();
        s.LastVersionSeen.ShouldBe("");
    }

    [Fact]
    public void Save_RoundTrips_NewFields()
    {
        var store = new SettingsStore(_path);
        var s = store.Load() with
        {
            AutostartEnabled = true,
            OnboardingCompleted = true,
            SpeakerFilterEnabled = true,
            LastVersionSeen = "0.3.0",
        };
        store.Save(s);
        var loaded = new SettingsStore(_path).Load();
        loaded.AutostartEnabled.ShouldBeTrue();
        loaded.OnboardingCompleted.ShouldBeTrue();
        loaded.SpeakerFilterEnabled.ShouldBeTrue();
        loaded.LastVersionSeen.ShouldBe("0.3.0");
    }

    [Fact]
    public void Load_CorruptFile_BacksUpAndReturnsDefaults()
    {
        File.WriteAllText(_path, "{ this is not valid json", System.Text.Encoding.UTF8);
        string? logged = null;
        var store = new SettingsStore(_path, msg => logged = msg);

        var s = store.Load();

        // Defaults returned (nothing is silently kept from the corrupt file).
        s.Schema.ShouldBe(1);
        s.OnboardingCompleted.ShouldBeFalse();

        // The corrupt file was moved aside to a .bad-* backup, not deleted.
        File.Exists(_path).ShouldBeFalse();
        var dir = Path.GetDirectoryName(_path)!;
        Directory.GetFiles(dir, $"{Path.GetFileName(_path)}.bad-*").Length.ShouldBe(1);

        // The caller was told.
        logged.ShouldNotBeNull();
    }

    [Fact]
    public void Save_RoundTrips_WindowSize()
    {
        var store = new SettingsStore(_path);
        var s = store.Load() with { WindowWidth = 640, WindowHeight = 520 };
        store.Save(s);
        var loaded = new SettingsStore(_path).Load();
        loaded.WindowWidth.ShouldBe(640);
        loaded.WindowHeight.ShouldBe(520);
    }

    [Fact]
    public void Defaults_Have_No_Persisted_WindowSize()
    {
        var s = new SettingsStore(_path).Load();
        s.WindowWidth.ShouldBeNull();
        s.WindowHeight.ShouldBeNull();
    }

    [Fact]
    public void Load_PreUpgradeFile_Without_Cleanup_Keys_Gets_Cleanup_Defaults()
    {
        // UPGRADE PATH: a settings.json written by a build BEFORE the cleanup
        // settings existed (real old-shape serialized output — camelCase, no
        // cleanup* keys). Loading it must fill all six cleanup fields with
        // their defaults, not throw and not zero them out.
        File.WriteAllText(_path, """
            {
              "schema": 1,
              "micDeviceId": "{abc-123}",
              "asrModelName": "parakeet-tdt-0.6b-v3",
              "asrProvider": "local",
              "assemblyAiModel": "universal-3-5-pro",
              "assemblyAiDeleteAfterTranscribe": true,
              "assemblyAiCloudDeadlineSeconds": 10,
              "assemblyAiKeytermsEnabled": false,
              "cleanupModelName": "qwen2.5-0.5b-instruct-q4_k_m",
              "holdHotkey": "RightCtrl+RightShift",
              "toggleHotkey": "Ctrl+Shift+Space",
              "playSounds": false,
              "autostartEnabled": false,
              "onboardingCompleted": true,
              "speakerFilterEnabled": false,
              "postPasteLearningEnabled": false,
              "prewarmMicEnabled": true,
              "lastVersionSeen": "0.4.0",
              "windowWidth": null,
              "windowHeight": null
            }
            """, System.Text.Encoding.UTF8);

        var s = new SettingsStore(_path).Load();

        s.CleanupEnabled.ShouldBeTrue();
        s.CleanupWindowContextEnabled.ShouldBeFalse();
        s.CleanupProfile.ShouldBe("Ordinary");
        s.CleanupCustomPrompt.ShouldBe("");
        s.CleanupMaxNewTokens.ShouldBe(512);
        s.CleanupTimeoutMs.ShouldBe(15000);

        // The old file's own values still load — proof this exercised the real
        // deserializer, not a defaults fallback from a rejected file.
        s.MicDeviceId.ShouldBe("{abc-123}");
        s.OnboardingCompleted.ShouldBeTrue();
        s.PlaySounds.ShouldBeFalse();
        s.LastVersionSeen.ShouldBe("0.4.0");
    }

    [Fact]
    public void StreamingEnabled_RoundTrips_ThroughStore()
    {
        var store = new SettingsStore(_path);
        store.Save(store.Load() with { StreamingEnabled = false });
        new SettingsStore(_path).Load().StreamingEnabled.ShouldBeFalse();
    }

    [Fact]
    public void StreamingEnabled_MissingFromOlderFile_DefaultsTrue()
    {
        // UPGRADE PATH: a settings.json written by a build BEFORE StreamingEnabled
        // existed (real old-shape serialized output — camelCase, no streamingEnabled
        // key). Loading it must fill the default, not throw and not fail the load.
        // The default is TRUE (2026-07-25): safe in every configuration — see the
        // AppSettings.StreamingEnabled comment for the per-provider behavior.
        File.WriteAllText(_path, """
            {
              "schema": 1,
              "micDeviceId": "{abc-123}",
              "asrModelName": "parakeet-tdt-0.6b-v3",
              "asrProvider": "assemblyai",
              "assemblyAiModel": "universal-3-5-pro",
              "assemblyAiDeleteAfterTranscribe": true,
              "assemblyAiCloudDeadlineSeconds": 10,
              "assemblyAiKeytermsEnabled": false,
              "cleanupModelName": "qwen2.5-0.5b-instruct-q4_k_m",
              "holdHotkey": "RightCtrl+RightShift",
              "toggleHotkey": "Ctrl+Shift+Space",
              "playSounds": false,
              "autostartEnabled": false,
              "onboardingCompleted": true,
              "speakerFilterEnabled": false,
              "postPasteLearningEnabled": false,
              "prewarmMicEnabled": true,
              "lastVersionSeen": "0.4.0"
            }
            """, System.Text.Encoding.UTF8);

        var s = new SettingsStore(_path).Load();

        s.StreamingEnabled.ShouldBeTrue();

        // The old file's own values still load — proof this exercised the real
        // deserializer, not a defaults fallback from a rejected file.
        s.MicDeviceId.ShouldBe("{abc-123}");
        s.AsrProvider.ShouldBe("assemblyai");
        s.PlaySounds.ShouldBeFalse();
    }

    [Fact]
    public void PostPasteLearning_Defaults_Off_And_RoundTrips()
    {
        new SettingsStore(_path).Load().PostPasteLearningEnabled.ShouldBeFalse();

        var store = new SettingsStore(_path);
        store.Save(store.Load() with { PostPasteLearningEnabled = true });
        new SettingsStore(_path).Load().PostPasteLearningEnabled.ShouldBeTrue();
    }
}

internal static class PipeExtensions
{
    public static TOut Pipe<TIn, TOut>(this TIn input, Func<TIn, TOut> fn) => fn(input);
}
