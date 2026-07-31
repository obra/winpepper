#if WINDOWS
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
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
    private int _keepAliveTick;
    private Windows.Graphics.RectInt32 _lastPositionedWorkArea;
    private double _pulsePhase;
    private PillAnimationMode _animMode = PillAnimationMode.None;

    // Wave-style voice meter: bars are generated at construction (BuildMeterBars)
    // and animated per tick from VoiceMeter.BarHeights. Each bar keeps ONE
    // SolidColorBrush whose Color is mutated in place — no per-tick allocations.
    private const int MeterBarCount = 12;
    private const double MeterBarMinPx = 3;   // resting stub height
    private const double MeterBarMaxPx = 22;  // full-scale height
    private Rectangle[] _meterBars = Array.Empty<Rectangle>();
    private SolidColorBrush[] _meterBrushes = Array.Empty<SolidColorBrush>();
    private int _meterTick;

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
        BuildMeterBars();

        // Step 1: realize HWND.
        _hwnd = WindowNative.GetWindowHandle(this);

        // Step 2: apply WS_EX_LAYERED then WS_EX_TRANSPARENT (see plan §13.3 note).
        ExtendedWindowStyle.MakeClickThroughTopmostTool(_hwnd, alpha: 230);

        // Step 3: AppWindow tweaks for frameless top-most.
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        // Same real app icon as MainWindow. The pill is borderless and hidden
        // from switchers today, so this is latent-proofing: some Windows
        // builds still surface a window icon in Alt-Tab/taskbar variants.
        // System.IO qualified: this file also sees Microsoft.UI.Xaml.Shapes.Path.
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(iconPath))
            appWindow.SetIcon(iconPath);

        appWindow.IsShownInSwitchers = false;
        if (appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsResizable = false;
            p.SetBorderAndTitleBar(false, false);
        }
        ExtendedWindowStyle.RemoveSystemBorder(_hwnd);
        ApplyLayout(appWindow, ExtendedWindowStyle.GetWindowDpi(_hwnd));

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); _visible = false; appWindow.Hide(); };

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _tickTimer.Tick += (_, _) =>
        {
            _vm.Tick();
            ElapsedText.Text = $"{_vm.ElapsedMs / 1000} s";

            // Pegged meter: decision is made in SessionViewModel on tick 4 (~400 ms
            // after recording start) and stays fixed for the pill's lifetime; read on
            // the tick like ElapsedMs/InputLevel (no new notification path, no
            // per-tick allocations).
            PeggedIndicator.Visibility =
                _vm.CpuPegged == true ? Visibility.Visible : Visibility.Collapsed;

            // Cheap: keep us pinned to the top even if another topmost window
            // was created after our last show. Only while visible. This tick
            // now also runs during PendingPaste/Error (PillTimerPolicy), so
            // the pill survives other topmost windows appearing while it
            // waits -- the 2026-07-28 buried-pill fix.
            if (_visible)
            {
                ExtendedWindowStyle.AssertTopmost(_hwnd);
                MaybeFollowForegroundMonitor();
            }

            // Pulse/meter rendering only where the policy says so; for
            // PendingPaste/Error the None-mode frame writes constants anyway,
            // but gating here makes "no pulse while pending" explicit.
            if (_previewActive || PillTimerPolicy.ForStage(_vm.Stage).AnimationRunning)
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
                    // Don't hide an active preview, and don't hide a pill a REAL
                    // state transition has already shown: realization can complete
                    // AFTER the pipeline showed the pill (e.g. a session starting
                    // during startup), and this one-shot hide must lose that race.
                    if (!_previewActive && !_visible) appWindow.Hide();
                }
                if (root.IsLoaded) { if (!_previewActive && !_visible) appWindow.Hide(); }
                else { root.Loaded += OnLoaded; }
            }
            else
            {
                this.DispatcherQueue.TryEnqueue(() => { if (!_previewActive && !_visible) appWindow.Hide(); });
            }
        });
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Preview mode owns the pill: stage/status churn from the real pipeline
        // (e.g. startup settling into Idle) must not hide or restyle it.
        if (_previewActive) return;
        if (e.PropertyName is not (nameof(SessionViewModel.Stage) or nameof(SessionViewModel.StatusText))) return;

        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        StatusText.Text = _vm.StatusText;
        _animMode = PillAnimationMap.ForStage(_vm.Stage);

        // Timer policy: the keep-alive tick runs whenever the pill is on
        // screen (incl. PendingPaste and Error); the pulse itself is gated
        // per-tick by PillTimerPolicy.AnimationRunning.
        if (PillTimerPolicy.ForStage(_vm.Stage).KeepAliveRunning) _tickTimer.Start();
        else _tickTimer.Stop();

        if (_vm.Stage == SessionStage.PendingPaste)
        {
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
            _visible = false;
            ResetPillVisual();
            _hideTimer.Stop(); _hideTimer.Start();
        }
        else if (_vm.Stage == SessionStage.Error)
        {
            ExtendedWindowStyle.SetClickThrough(_hwnd, clickThrough: true);
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
                // Distinct from Transcribing's orange so the labeled
                // "Cleaning up..." phase is visually tellable at a glance.
                SessionStage.CleaningUp  => Microsoft.UI.Colors.MediumPurple,
                SessionStage.Injecting   => Microsoft.UI.Colors.LimeGreen,
                _ => Microsoft.UI.Colors.Gray,
            });
            SetMeterVisible(_vm.Stage == SessionStage.Recording);
            _pulsePhase = 0;
            PositionBottomCenter(appWindow);
            appWindow.Show(activateWindow: false);
            _visible = true;
            ExtendedWindowStyle.AssertTopmost(_hwnd);
            _hideTimer.Stop();
        }
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Only actionable in the PENDING state; other states are click-through.
        if (_vm.Stage != SessionStage.PendingPaste) return;
        e.Handled = true;
        // The handler injects into whatever field is focused NOW (the user's
        // explicit choice) and reports the outcome to the VM. On success the VM
        // returns to Idle, which hides the pill via OnVmChanged's Idle arm.
        PastePendingHandler?.Invoke();
    }

    private void ResetPillVisual()
    {
        Dot.Opacity = 1.0;
        DotScale.ScaleX = 1.0;
        DotScale.ScaleY = 1.0;
        SetMeterVisible(false);
    }

    /// <summary>
    /// Create the wave-meter bars once. Each bar owns one SolidColorBrush that
    /// is mutated in place every tick (no per-tick allocations).
    /// </summary>
    private void BuildMeterBars()
    {
        _meterBars = new Rectangle[MeterBarCount];
        _meterBrushes = new SolidColorBrush[MeterBarCount];
        for (var i = 0; i < MeterBarCount; i++)
        {
            _meterBrushes[i] = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            _meterBars[i] = new Rectangle
            {
                Width = 3,
                Height = MeterBarMinPx,
                RadiusX = 1.5,
                RadiusY = 1.5,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = _meterBrushes[i],
            };
            MeterPanel.Children.Add(_meterBars[i]);
        }
    }

    /// <summary>
    /// Render one wave frame: bar heights from VoiceMeter.BarHeights, colour
    /// lerped teal -> amber -> red with the bar's own height. Cheap: 12 height
    /// writes + 12 in-place Color writes per 100 ms tick.
    /// </summary>
    private void UpdateMeterWave(double[] heights)
    {
        for (var i = 0; i < _meterBars.Length && i < heights.Length; i++)
        {
            var h = heights[i];
            _meterBars[i].Height = MeterBarMinPx + (h * (MeterBarMaxPx - MeterBarMinPx));
            _meterBrushes[i].Color = WaveColor(h);
        }
    }

    /// <summary>Teal (quiet) -> amber (speaking) -> red (loud) by bar height.</summary>
    private static Windows.UI.Color WaveColor(double h)
    {
        static byte Lerp(byte a, byte b, double t) => (byte)(a + ((b - a) * t));
        // teal #2DD4BF -> amber #F59E0B -> red #EF4444
        if (h < 0.05) return Windows.UI.Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF); // resting stub
        if (h < 0.5)
        {
            var t = h / 0.5;
            return Windows.UI.Color.FromArgb(0xFF, Lerp(0x2D, 0xF5, t), Lerp(0xD4, 0x9E, t), Lerp(0xBF, 0x0B, t));
        }
        var u = (h - 0.5) / 0.5;
        return Windows.UI.Color.FromArgb(0xFF, Lerp(0xF5, 0xEF, u), Lerp(0x9E, 0x44, u), Lerp(0x0B, 0x44, u));
    }

    private static readonly double[] MeterAtRest = new double[MeterBarCount];

    /// <summary>Show or hide the meter; hiding also drops all bars to rest.</summary>
    private void SetMeterVisible(bool visible)
    {
        MeterPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) UpdateMeterWave(MeterAtRest);
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
                // Perceptual (dB) mapping: raw peak level for speech sits at
                // ~0.05..0.3 linear, which a linear meter renders as one stuck
                // bar. Perceptual() spreads normal speech across the range.
                var level = _previewActive ? NextPreviewLevel() : VoiceMeter.Perceptual(_vm.InputLevel);
                var scale = 1.0 + (level * 0.8); // 1.0 .. 1.8
                DotScale.ScaleX = scale;
                DotScale.ScaleY = scale;
                Dot.Opacity = 1.0;
                UpdateMeterWave(VoiceMeter.BarHeights(level, _meterTick++, MeterBarCount));
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

    /// <summary>
    /// Re-anchors the pill when the FOREGROUND window moved to a different
    /// monitor while the pill is on screen (PendingPaste persists across
    /// window switches, so it must follow the user's active display). Runs
    /// on the 100 ms keep-alive tick but throttled to ~1 s, and calls
    /// PositionBottomCenter (2x Move + resize -- not cheap) ONLY when the
    /// target work area actually changed, so the clickable pill is never
    /// repositioned under a user's pointer on the same monitor.
    /// </summary>
    private void MaybeFollowForegroundMonitor()
    {
        if (++_keepAliveTick % 10 != 0) return; // 100 ms tick -> ~1 s cadence
        var fgHwnd = Native.ForegroundWindow.GetForegroundWindow();
        if (fgHwnd == IntPtr.Zero) return;
        var work = DisplayArea.GetFromWindowId(
            Win32Interop.GetWindowIdFromWindow(fgHwnd), DisplayAreaFallback.Nearest).WorkArea;
        if (work.X == _lastPositionedWorkArea.X && work.Y == _lastPositionedWorkArea.Y
            && work.Width == _lastPositionedWorkArea.Width && work.Height == _lastPositionedWorkArea.Height)
        {
            return;
        }
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        PositionBottomCenter(appWindow);
    }

    private void PositionBottomCenter(AppWindow appWindow)
    {
        var fgHwnd = Native.ForegroundWindow.GetForegroundWindow();
        var display = fgHwnd != IntPtr.Zero
            ? DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(fgHwnd), DisplayAreaFallback.Nearest)
            : DisplayArea.Primary;
        var work = display.WorkArea;
        _lastPositionedWorkArea = work;

        // Enter the target display before sizing. Otherwise a pill sized with the target
        // DPI while still on another monitor can be scaled a second time by WM_DPICHANGED.
        var targetDpiHwnd = fgHwnd != IntPtr.Zero ? fgHwnd : _hwnd;
        var targetLayout = StatusPillLayout.ForDpi(ExtendedWindowStyle.GetWindowDpi(targetDpiHwnd));
        var provisionalX = work.X + (work.Width - appWindow.Size.Width) / 2;
        var provisionalY = work.Y + work.Height - appWindow.Size.Height - targetLayout.BottomGap;
        appWindow.Move(new PointInt32(provisionalX, provisionalY));

        var layout = ApplyLayout(appWindow, ExtendedWindowStyle.GetWindowDpi(_hwnd));
        var x = work.X + (work.Width  - appWindow.Size.Width)  / 2;
        var y = work.Y + work.Height - appWindow.Size.Height - layout.BottomGap;
        appWindow.Move(new PointInt32(x, y));
    }

    private StatusPillPixelLayout ApplyLayout(AppWindow appWindow, uint dpi)
    {
        var layout = StatusPillLayout.ForDpi(dpi);
        appWindow.ResizeClient(new SizeInt32(layout.ClientWidth, layout.ClientHeight));
        // No SetWindowRgn here: window regions are IGNORED on layered windows
        // (WS_EX_LAYERED + SetLayeredWindowAttributes), which this window is —
        // that's why the old region clip left a visible light rectangle. The
        // capsule silhouette comes from LWA_COLORKEY instead: the XAML root
        // Grid paints pure black (#000000) and MakeClickThroughTopmostTool
        // keys that colour out, leaving only the capsule Border visible.
        return layout;
    }

    // ---- Preview mode (WINPEPPER_PILL_PREVIEW=1) ------------------------
    // Forces the pill visible in the Recording visual state with a synthetic
    // level sweep so the meter/dot animate without any audio. Used by the
    // on-device visual verification loop (screenshot + pixel probe).
    private bool _previewActive;
    private double _previewPhase;

    private double NextPreviewLevel()
    {
        _previewPhase += 2 * Math.PI / 30.0; // ~3 s full sweep at 100 ms ticks
        return (Math.Sin(_previewPhase) + 1.0) / 2.0;
    }

    public void StartPreview(Microsoft.Extensions.Logging.ILogger log)
    {
        _previewActive = true;
        _animMode = PillAnimationMode.VoiceLevel;
        StatusText.Text = "Recording...";
        Dot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Red);
        SetMeterVisible(true);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        PositionBottomCenter(appWindow);
        appWindow.Show(activateWindow: false);
        _visible = true;
        // Re-apply the border/corner treatment AFTER the window is shown: the
        // presenter/show pipeline can reset styles, and applying to a visible
        // window also produces fresh diagnostics for the on-device probe.
        ExtendedWindowStyle.RemoveSystemBorder(_hwnd);
        ExtendedWindowStyle.AssertTopmost(_hwnd);
        _tickTimer.Start();
        _hideTimer.Stop();
        var pos = appWindow.Position;
        var size = appWindow.Size;
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(log,
            "Pill preview shown at {X},{Y} {W}x{H} (dpi {Dpi})",
            pos.X, pos.Y, size.Width, size.Height, ExtendedWindowStyle.GetWindowDpi(_hwnd));
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(log,
            "Pill border diagnostics: {Diag}", ExtendedWindowStyle.LastBorderDiagnostics);
    }
}
#endif
