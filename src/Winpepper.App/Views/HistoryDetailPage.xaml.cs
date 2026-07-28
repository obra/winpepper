#if WINDOWS
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Winpepper.History;
using Winpepper.History.ViewModels;
using Winpepper.Models;

namespace Winpepper.App.Views;

public sealed partial class HistoryDetailPage : Page
{
    public HistoryDetailViewModel? ViewModel { get; private set; }
    public IReadOnlyList<ModelDescriptor> AvailableAsrModels { get; private set; } = Array.Empty<ModelDescriptor>();
    public IReadOnlyList<ModelDescriptor> AvailableCleanupModels { get; private set; } = Array.Empty<ModelDescriptor>();

    // Custom audio player state. MediaPlayer is the headless engine behind
    // MediaPlayerElement; using it directly lets us draw our own minimal
    // transport (Play/Seek/Time) instead of the default chrome that includes
    // Cast-to-Device, Aspect Ratio, and Full-Window — none of which make
    // sense for a few-second dictation clip.
    private MediaPlayer? _player;
    private DispatcherQueueTimer? _positionTimer;
    private bool _suppressSeek;
    private const string GlyphPlay = "";
    private const string GlyphPause = "";
    private const string GlyphVolume = "";
    private const string GlyphMute = "";

    public HistoryDetailPage()
    {
        this.InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        // Task 23 routes the "Lab" nav-rail click to this page with no
        // parameter; only a row click from HistoryPage hands us an entry.
        // Show the empty-state hint instead of dead editor chrome (issue #21).
        if (e.Parameter is not HistoryEntry entry)
        {
            EditorRoot.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Visible;
            return;
        }
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        EditorRoot.Visibility = Visibility.Visible;

        var history = App.Shell!.HistoryServices;
        var models = App.Shell!.ModelsServices;

        AvailableAsrModels = models.Registry.ByKind(ModelKind.Asr).ToList();
        AvailableCleanupModels = models.Registry.ByKind(ModelKind.Cleanup).ToList();

        ViewModel = new HistoryDetailViewModel(
            entry, history.HistoryRoot,
            history.TranscriptionRerun, history.CleanupRerun,
            promoteAsrDefault: name =>
            {
                var shell = App.Shell!;
                shell.AsrModelSelection.Publish(name); // effective immediately
                _ = shell.SettingsWriter.QueueAndFlushAsync(s2 => s2 with { AsrModelName = name }); // durability
            },
            promoteCleanupDefault: name =>
            {
                var shell = App.Shell!;
                shell.CleanupModelSelection.Publish(name); // effective immediately (next dictation)
                shell.CleanupBackend.RequestPrewarm();     // background load so the next dictation doesn't pay it
                _ = shell.SettingsWriter.QueueAndFlushAsync(s2 => s2 with { CleanupModelName = name }); // durability
            });

        OriginalTranscriptText.Text = ViewModel.OriginalTranscript;
        OriginalCleanedText.Text = ViewModel.OriginalCleanedText;

        try
        {
            var wavPath = ViewModel.WavAbsolutePath;
            var file = await StorageFile.GetFileFromPathAsync(wavPath);
            InitializeAudioPlayer(MediaSource.CreateFromStorageFile(file));
        }
        catch (Exception)
        {
            // No audio — leave the player row disabled.
        }

        // Bind diffs.
        ViewModel.TranscriptionPanel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == "Diff")
                TranscriptionDiff.Segments = ViewModel.TranscriptionPanel.Diff;
        };
        ViewModel.CleanupPanel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == "Diff")
                CleanupDiff.Segments = ViewModel.CleanupPanel.Diff;
        };
    }

    private void OnGoToHistory(object sender, RoutedEventArgs e) =>
        App.Shell?.Main.NavigateToTag("history");

    private void OnAsrSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null) return;
        if (AsrModelPicker.SelectedItem is ModelDescriptor d)
        {
            ViewModel.TranscriptionPanel.SelectedModelName = d.Name;
            var models = App.Shell!.ModelsServices;
            ViewModel.TranscriptionPanel.SelectedModelDirectory =
                Path.Combine(models.ModelsRoot, d.InstallDirRelative);
        }
    }

    private void OnCleanupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null) return;
        if (CleanupModelPicker.SelectedItem is ModelDescriptor d)
        {
            ViewModel.CleanupPanel.SelectedModelName = d.Name;
            var models = App.Shell!.ModelsServices;
            var dir = Path.Combine(models.ModelsRoot, d.InstallDirRelative);
            var file = d.Files.FirstOrDefault();
            ViewModel.CleanupPanel.SelectedModelDirectory = dir;
            ViewModel.CleanupPanel.SelectedModelPath =
                file is null ? "" : Path.Combine(dir, file.RelativePath);
            ViewModel.CleanupPanel.SelectedModelPromptFormat = d.PromptFormat;
            ViewModel.CleanupPanel.SelectedModelOmitPromptExample = d.OmitPromptExample;
        }
    }

    private async void OnRunTranscriptionRerun(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.TranscriptionPanel.RunAsync(CancellationToken.None);
    }

    private async void OnRunCleanupRerun(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.CleanupCustomPrompt = CustomPromptBox.Text;
        ViewModel.IncludeWindowContextInRerun = WindowContextToggle.IsOn;
        await ViewModel.CleanupPanel.RunAsync(CancellationToken.None);
    }

    private async void OnShowCleanupTranscript(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var dlg = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Cleanup transcript",
            CloseButtonText = "Close",
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = $"=== Assembled prompt ===\n{ViewModel.CleanupAssembledPrompt}\n\n=== Raw model output ===\n{ViewModel.CleanupRawOutput}",
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                },
                Height = 480,
            },
        };
        await dlg.ShowAsync();
    }

    private void OnPromoteAsr(object sender, RoutedEventArgs e) => ViewModel?.PromoteTranscriptionRerunAsDefault();
    private void OnPromoteCleanup(object sender, RoutedEventArgs e) => ViewModel?.PromoteCleanupRerunAsDefault();

    private void InitializeAudioPlayer(MediaSource source)
    {
        _player = new MediaPlayer { Source = source, AutoPlay = false };
        var dispatcher = DispatcherQueue.GetForCurrentThread();

        _player.MediaOpened += (_, _) => dispatcher.TryEnqueue(() =>
        {
            var d = _player?.PlaybackSession.NaturalDuration ?? TimeSpan.Zero;
            DurationText.Text = FormatTime(d);
            PlayPauseBtn.IsEnabled = d > TimeSpan.Zero;
            SeekSlider.IsEnabled = d > TimeSpan.Zero;
            MuteBtn.IsEnabled = d > TimeSpan.Zero;
        });
        _player.MediaEnded += (_, _) => dispatcher.TryEnqueue(() =>
        {
            PlayPauseIcon.Glyph = GlyphPlay;
            _suppressSeek = true;
            try { SeekSlider.Value = 0; } finally { _suppressSeek = false; }
            PositionText.Text = FormatTime(TimeSpan.Zero);
        });
        _player.PlaybackSession.PlaybackStateChanged += (s, _) => dispatcher.TryEnqueue(() =>
            PlayPauseIcon.Glyph = s.PlaybackState == MediaPlaybackState.Playing ? GlyphPause : GlyphPlay);

        // Poll position 10x/sec while playing; cheaper than wiring PositionChanged
        // which fires every frame and forces dispatcher hops.
        _positionTimer = dispatcher.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(100);
        _positionTimer.Tick += (_, _) =>
        {
            if (_player is null) return;
            var pos = _player.PlaybackSession.Position;
            var dur = _player.PlaybackSession.NaturalDuration;
            PositionText.Text = FormatTime(pos);
            if (dur > TimeSpan.Zero)
            {
                _suppressSeek = true;
                try { SeekSlider.Value = pos.TotalSeconds / dur.TotalSeconds; } finally { _suppressSeek = false; }
            }
        };
        _positionTimer.Start();
    }

    private void OnPlayPauseClicked(object sender, RoutedEventArgs e)
    {
        if (_player is null) return;
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause();
        else _player.Play();
    }

    private void OnMuteClicked(object sender, RoutedEventArgs e)
    {
        if (_player is null) return;
        _player.IsMuted = !_player.IsMuted;
        MuteIcon.Glyph = _player.IsMuted ? GlyphMute : GlyphVolume;
    }

    private void OnSeekChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSeek || _player is null) return;
        var dur = _player.PlaybackSession.NaturalDuration;
        if (dur <= TimeSpan.Zero) return;
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue * dur.TotalSeconds);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _positionTimer?.Stop();
        _positionTimer = null;
        _player?.Dispose();
        _player = null;
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }
}
#endif
