#if WINDOWS
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Winpepper.App.Views.Native;
using Winpepper.Core.ViewModels;
using WinRT.Interop;

namespace Winpepper.App.Views;

public sealed partial class StatusPillWindow : Window
{
    private readonly SessionViewModel _vm;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _tickTimer;
    private IntPtr _hwnd;

    public StatusPillWindow(SessionViewModel vm)
    {
        _vm = vm;
        InitializeComponent();

        // Step 1: realize HWND.
        _hwnd = WindowNative.GetWindowHandle(this);

        // Step 2: apply WS_EX_LAYERED then WS_EX_TRANSPARENT (see plan §13.3 note).
        ExtendedWindowStyle.MakeClickThroughTopmostTool(_hwnd, alpha: 230);

        // Step 3: AppWindow tweaks for frameless top-most.
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        appWindow.IsShownInSwitchers = false;
        if (appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsResizable = false;
            p.SetBorderAndTitleBar(false, false);
        }
        appWindow.Resize(new SizeInt32(260, 44));

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); appWindow.Hide(); };

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _tickTimer.Tick += (_, _) => { _vm.Tick(); ElapsedText.Text = $"{_vm.ElapsedMs} ms"; };

        _vm.PropertyChanged += OnVmChanged;
        appWindow.Hide();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SessionViewModel.Stage) or nameof(SessionViewModel.StatusText))) return;

        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        StatusText.Text = _vm.StatusText;

        if (_vm.Stage == SessionStage.Idle)
        {
            _tickTimer.Stop();
            _hideTimer.Stop(); _hideTimer.Start();
        }
        else if (_vm.Stage == SessionStage.Error)
        {
            _tickTimer.Stop();
            Dot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Goldenrod);
            PositionBottomCenter(appWindow);
            appWindow.Show(activateWindow: false);
            _hideTimer.Stop();
        }
        else
        {
            Dot.Fill = new SolidColorBrush(_vm.Stage switch
            {
                SessionStage.Recording   => Microsoft.UI.Colors.Red,
                SessionStage.Transcribing => Microsoft.UI.Colors.Orange,
                SessionStage.CleaningUp  => Microsoft.UI.Colors.Orange,
                SessionStage.Injecting   => Microsoft.UI.Colors.LimeGreen,
                _ => Microsoft.UI.Colors.Gray,
            });
            PositionBottomCenter(appWindow);
            appWindow.Show(activateWindow: false);
            _tickTimer.Start();
            _hideTimer.Stop();
        }
    }

    private void PositionBottomCenter(AppWindow appWindow)
    {
        var fgHwnd = Native.ForegroundWindow.GetForegroundWindow();
        var display = fgHwnd != IntPtr.Zero
            ? DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(fgHwnd), DisplayAreaFallback.Nearest)
            : DisplayArea.Primary;
        var work = display.WorkArea;
        var x = work.X + (work.Width  - appWindow.Size.Width)  / 2;
        var y = work.Y +  work.Height - appWindow.Size.Height - 48;
        appWindow.Move(new PointInt32(x, y));
    }
}
#endif
