#if WINDOWS
using System.ComponentModel;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Winpepper.Core.ViewModels;

namespace Winpepper.App.Tray;

public sealed class TrayIconHost : IDisposable
{
    private readonly SessionViewModel _session;
    private readonly TaskbarIcon _icon;
    private readonly TrayMenu _menu;
    private readonly Action _openSettings;
    private readonly Action _quit;
    private bool _paused;

    public TrayIconHost(SessionViewModel session, string assetsDir, string versionString,
                        Action openSettings, Action quit)
    {
        _session = session;
        _openSettings = openSettings;
        _quit = quit;
        _menu = new TrayMenu();
        _icon = new TaskbarIcon
        {
            ToolTipText = "Winpepper - Ready",
            IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(assetsDir, "AppIcon.ico"))),
            ContextFlyout = _menu,
            NoLeftClickDelay = true,
        };
        _icon.LeftClickCommand = new SimpleCommand(openSettings);
        _menu.SettingsItem.Click += (_, _) => openSettings();
        _menu.PauseToggle.Click += (_, _) =>
        {
            // Pause is a UI-only label change. Don't go through NotifyError —
            // that channel sets Stage = Error and paints the pill yellow.
            _paused = _menu.PauseToggle.IsChecked;
            UpdateFromSession();
        };
        _menu.QuitMenuItem.Click += (_, _) => quit();
        _menu.VersionLabel.Text = $"Winpepper v{versionString}";
        _session.PropertyChanged += OnSessionChanged;
        UpdateFromSession();
    }

    public bool IsPaused => _paused;

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionViewModel.Stage)
                           or nameof(SessionViewModel.StatusText)
                           or nameof(SessionViewModel.LastErrorMessage))
            UpdateFromSession();
    }

    private void UpdateFromSession()
    {
        var state = Winpepper.Core.Tray.TrayIconStateMapper.Map(
            _session.Stage, _session.LastErrorMessage, _paused);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", state.IconName);
        if (File.Exists(iconPath))
            _icon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
        _menu.StatusItemControl.Text = _paused ? "Paused" : _session.StatusText;
        _icon.ToolTipText = state.Tooltip;
        _menu.StatusProgressBar.Visibility =
            !_paused && _session.Stage is SessionStage.Recording or SessionStage.Transcribing or SessionStage.CleaningUp
                ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Dispose()
    {
        _session.PropertyChanged -= OnSessionChanged;
        _icon.Dispose();
    }

    private sealed class SimpleCommand : System.Windows.Input.ICommand
    {
        private readonly Action _action;
        public SimpleCommand(Action action) { _action = action; }
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? _) => true;
        public void Execute(object? _) => _action();
    }
}
#endif
