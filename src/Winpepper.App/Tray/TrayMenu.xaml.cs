using Microsoft.UI.Xaml.Controls;

namespace Winpepper.App.Tray;

public sealed partial class TrayMenu : MenuFlyout
{
    public TrayMenu() { InitializeComponent(); }

    public MenuFlyoutItem StatusItemControl => StatusItem;
    public ProgressBar StatusProgressBar => StatusProgress;
    public MenuFlyoutItem SettingsItem => OpenSettings;
    public ToggleMenuFlyoutItem PauseToggle => PauseItem;
    public MenuFlyoutItem QuitMenuItem => QuitItem;
    public MenuFlyoutItem VersionLabel => VersionItem;
}
