#if WINDOWS
using System.ComponentModel;
using H.NotifyIcon;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger _log;
    private bool _paused;

    public TrayIconHost(SessionViewModel session, string assetsDir, string versionString,
                        Action openSettings, Action quit, ILogger<TrayIconHost>? log = null)
    {
        _session = session;
        _openSettings = openSettings;
        _quit = quit;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TrayIconHost>.Instance;
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
        _menu.CrashTestMenuItem.Click += (_, _) =>
            throw new InvalidOperationException("synthetic crash from tray menu");
        _menu.VersionLabel.Text = $"Winpepper v{versionString}";
        _session.PropertyChanged += OnSessionChanged;
        UpdateFromSession();

        // H.NotifyIcon 2.x registers the Shell_NotifyIcon lazily; without an
        // explicit ForceCreate() the icon never appears and hide-to-tray
        // strands the app with no window (issue #10). Efficiency mode stays
        // off — it drops the process to EcoQoS, which hurts dictation latency.
        var iconPath = Path.Combine(assetsDir, "AppIcon.ico");
        if (!File.Exists(iconPath))
            _log.LogWarning("Tray icon asset missing: {IconPath}", iconPath);
        try
        {
            _icon.ForceCreate(enablesEfficiencyMode: false);
            IsRegistered = _icon.IsCreated;
            if (IsRegistered)
                _log.LogInformation("Tray icon registered");
            else
                _log.LogWarning("Tray icon not created after ForceCreate(); window close will exit instead of hiding to tray");
        }
        catch (Exception ex)
        {
            IsRegistered = false;
            _log.LogError(ex, "Tray icon registration failed; window close will exit instead of hiding to tray");
        }
    }

    /// <summary>
    /// True when Shell_NotifyIcon registration succeeded. When false the tray
    /// icon is not a usable recovery affordance, so closing the main window
    /// must exit the app rather than hide it (issue #10).
    /// </summary>
    public bool IsRegistered { get; }

    public bool IsPaused => _paused;

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionViewModel.Stage)
                           or nameof(SessionViewModel.StatusText)
                           or nameof(SessionViewModel.LastErrorMessage)
                           or nameof(SessionViewModel.ActiveConditionMessage))
            UpdateFromSession();
    }

    private void UpdateFromSession()
    {
        // The tray is the persistent surface for an ongoing CONDITION: the pill
        // retires after its attention-grab window, the tray keeps it until a
        // recovery success clears it.
        var state = Winpepper.Core.Tray.TrayIconStateMapper.Map(
            _session.Stage, _session.LastErrorMessage, _paused, _session.ActiveConditionMessage);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", state.IconName);
        if (File.Exists(iconPath))
            _icon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
        _menu.StatusItemControl.Text = _paused ? "Paused" : _session.StatusText;
        _icon.ToolTipText = state.Tooltip;
        // Tray progress indicator dropped - MenuFlyout doesn't accept ProgressBar
        // children. Live progress is shown by the status pill instead.
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
