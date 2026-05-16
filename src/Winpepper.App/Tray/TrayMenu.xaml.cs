using Microsoft.UI.Xaml.Controls;

namespace Winpepper.App.Tray;

public sealed partial class TrayMenu : MenuFlyout
{
    public TrayMenu()
    {
        InitializeComponent();
#if DEBUG
        CrashTestItem.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
#endif
    }

    public MenuFlyoutItem StatusItemControl => StatusItem;
    public MenuFlyoutItem SettingsItem => OpenSettings;
    public ToggleMenuFlyoutItem PauseToggle => PauseItem;
    public MenuFlyoutItem QuitMenuItem => QuitItem;
    public MenuFlyoutItem VersionLabel => VersionItem;
    public MenuFlyoutItem CrashTestMenuItem => CrashTestItem;
}
