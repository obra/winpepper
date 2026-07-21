#if WINDOWS
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
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
    private bool _visible;
    private double _pulsePhase;
    private PillAnimationMode _animMode = PillAnimationMode.None;

    /// <summary>
    /// Invoked when the user clicks the pill while it is in the PENDING state.
    /// Wired by AppShell to perform the paste at click time. Returns true when
    /// the paste succeeded (slot consumed), false when it failed (slot kept).
    /// </summary>
    public Func<bool>? PastePendingHandler { get; set; }

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
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); _visible = false; appWindow.Hide(); };

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _tickTimer.Tick += (_, _) =>
        {
            _vm.Tick();
            ElapsedText.Text = $"{_vm.ElapsedMs} ms";

            // Cheap: keep us pinned to the top even if another topmost window
            // was created after our last show. Only while visible.
            if (_visible) ExtendedWindowStyle.AssertTopmost(_hwnd);

            ApplyAnimationFrame();
        };

        _vm.PropertyChanged += OnVmChanged;
        appWindow.Hide();
    }

    /// <summary>
    /// One-time realization of the WinUI content island so later Show() calls
    /// present LIVE content (animations, elapsed-ms). Without this the pill
    /// renders its first frame and freezes. MUST run off the bootstrap call
    /// stack: Activate() starts async island composition, and hiding in the
    /// same pump tears it down mid-composition -> WinRT stowed E_POINTER
    /// (0xc000027b) in Microsoft.UI.Xaml and process death (see 2d1a607 crash).
    /// So: enqueue at Low priority, Activate, then hide only after the content
    /// has Loaded (island realized).
    /// </summary>
    public void RealizeOnce()
    {
        this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
            this.Activate();
            if (this.Content is FrameworkElement root)
            {
                void OnLoaded(object s, RoutedEventArgs e)
                {
                    root.Loaded -= OnLoaded;
                    appWindow.Hide();
                }
                if (root.IsLoaded) { appWindow.Hide(); }
                else { root.Loaded += OnLoaded; }
            }
            else
            {
                this.DispatcherQueue.TryEnqueue(() => appWindow.Hide());
            }
        });
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SessionViewModel.Stage) or nameof(SessionViewModel.StatusText))) return;

        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        StatusText.Text = _vm.StatusText;
        _animMode = PillAnimationMap.ForStage(_vm.Stage);

        if (_vm.Stage == SessionStage.PendingPaste)
        {
            _tickTimer.Stop();               // no thinking pulse while waiting
            _visible = true;
            ResetPillVisual();               // steady dot, full opacity, scale 1
            Dot.Fill = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
            PositionBottomCenter(appWindow);
            appWindow.Show(activateWindow: false);
            ExtendedWindowStyle.AssertTopmost(_hwnd);
            ExtendedWindowStyle.SetClickThrough(_hwnd, clickThrough: false); // make pill clickable
            _hideTimer.Stop();               // never auto-hide while pending
            return;
        }

        if (_vm.Stage == SessionStage.Idle)
        {
            ExtendedWindowStyle.SetClickThrough(_hwnd, clickThrough: true);
            _tickTimer.Stop();
            _visible = false;
            ResetPillVisual();
            _hideTimer.Stop(); _hideTimer.Start();
        }
        else if (_vm.Stage == SessionStage.Error)
        {
            ExtendedWindowStyle.SetClickThrough(_hwnd, clickThrough: true);
            _tickTimer.Stop();
            _visible = true;
            ResetPillVisual(); // steady dot; Error keeps its Goldenrod colour below
            Dot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Goldenrod);
            PositionBottomCenter(appWindow);
            appWindow.Show(activateWindow: false);
            ExtendedWindowStyle.AssertTopmost(_hwnd);
            _hideTimer.Stop();
        }
        else
        {
            ExtendedWindowStyle.SetClickThrough(_hwnd, clickThrough: true);
            Dot.Fill = new SolidColorBrush(_vm.Stage switch
            {
                SessionStage.Recording   => Microsoft.UI.Colors.Red,
                SessionStage.Transcribing => Microsoft.UI.Colors.Orange,
                SessionStage.CleaningUp  => Microsoft.UI.Colors.Orange,
                SessionStage.Injecting   => Microsoft.UI.Colors.LimeGreen,
                _ => Microsoft.UI.Colors.Gray,
            });
            _pulsePhase = 0;
            PositionBottomCenter(appWindow);
            appWindow.Show(activateWindow: false);
            _visible = true;
            ExtendedWindowStyle.AssertTopmost(_hwnd);
            _tickTimer.Start();
            _hideTimer.Stop();
        }
    }

    private void ResetPillVisual()
    {
        Dot.Opacity = 1.0;
        DotScale.ScaleX = 1.0;
        DotScale.ScaleY = 1.0;
    }

    /// <summary>
    /// Per-tick (100 ms) visual update. VoiceLevel scales the dot from the
    /// smoothed input level; Thinking oscillates the dot opacity ~0.4..1.0 on a
    /// ~1 s loop; None leaves the dot static (scale 1, opacity 1).
    /// </summary>
    private void ApplyAnimationFrame()
    {
        switch (_animMode)
        {
            case PillAnimationMode.VoiceLevel:
                var scale = 1.0 + (_vm.InputLevel * 0.8); // 1.0 .. 1.8
                DotScale.ScaleX = scale;
                DotScale.ScaleY = scale;
                Dot.Opacity = 1.0;
                break;

            case PillAnimationMode.Thinking:
                // 100 ms tick, 10 ticks per ~1 s cycle.
                _pulsePhase += 2 * Math.PI / 10.0;
                var osc = (Math.Sin(_pulsePhase) + 1.0) / 2.0; // 0..1
                Dot.Opacity = 0.4 + (0.6 * osc);               // 0.4 .. 1.0
                DotScale.ScaleX = 1.0;
                DotScale.ScaleY = 1.0;
                break;

            default: // None
                Dot.Opacity = 1.0;
                DotScale.ScaleX = 1.0;
                DotScale.ScaleY = 1.0;
                break;
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
