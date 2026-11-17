using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ufo;

/// <summary>Órbita shell: the opening abduction and the orbit navigation between sections.</summary>
public partial class MainWindow
{
    private static Duration OrbitTurn => Motion(550);
    private static readonly IEasingFunction EaseOut = new QuinticEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };
    private static readonly IEasingFunction EaseInOut = new SineEase { EasingMode = EasingMode.EaseInOut };

    /// <summary>Where each section's moon sits on the ring, in degrees from the right-hand side.</summary>
    private static readonly Dictionary<string, double> MoonAngles = new() { ["fone"] = 0, ["mic"] = 120, ["eq"] = 240 };

    private bool isSplashDone;

    /// <summary>Accumulated ring rotation in degrees (clockwise positive); never wrapped.</summary>
    private double ringAngle;

    /// <summary>The layout was designed at this size; the interface scales from it.</summary>
    private const double DesignWidth = 1120;
    private const double DesignHeight = 740;

    /// <summary>
    /// Scales the interface with the window (by the tighter of width and height, 0.72×–1.6×) and
    /// keeps the opening scene to roughly the middle third of the window.
    /// </summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double contentHeight = Math.Max(1, e.NewSize.Height - 40);
        double scale = Math.Clamp(Math.Min(e.NewSize.Width / DesignWidth, contentHeight / DesignHeight), 0.72, 1.6);
        ShellScale.ScaleX = ShellScale.ScaleY = Math.Round(scale, 2);
        // The whole opening scene (360×300) fits about 38% of the width and 44% of the height.
        SplashBox.Width = Math.Clamp(Math.Min(e.NewSize.Width * 0.38, contentHeight * 0.44 * 1.2), 220, 560);
    }

    /// <summary>Below this rail width the paired groups (Fone, Microfone) stack instead of sitting side by side.</summary>
    private const double TwoColumnMinWidth = 600;

    private void OnContentRailSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool isWide = e.NewSize.Width >= TwoColumnMinWidth;
        foreach ((Grid columns, FrameworkElement second) in new (Grid, FrameworkElement)[] { (FoneColumns, FoneSecondGroup), (MicColumns, MicSecondGroup) })
        {
            FrameworkElement first = (FrameworkElement)columns.Children[0];
            Grid.SetColumnSpan(first, isWide ? 1 : 3);
            Grid.SetColumn(second, isWide ? 2 : 0);
            Grid.SetRow(second, isWide ? 0 : 1);
            Grid.SetColumnSpan(second, isWide ? 1 : 3);
        }
    }

    /// <summary>A UI motion duration, or zero when Windows animations are turned off.</summary>
    private static Duration Motion(double milliseconds) =>
        SystemParameters.ClientAreaAnimation ? TimeSpan.FromMilliseconds(milliseconds) : TimeSpan.Zero;

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        bool isMaximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = isMaximized ? "" : "";
        MaximizeButton.ToolTip = isMaximized ? "Restaurar" : "Maximizar";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        source?.AddHook(LimitMaximizeToWorkArea);
        source?.AddHook(OfferSnapLayouts);
    }

    /// <summary>
    /// Windows 11 shows the Snap Layouts flyout only when the maximize button answers WM_NCHITTEST as
    /// HTMAXBUTTON; Windows then also handles its click. Hover is mirrored to the button through Tag.
    /// </summary>
    private nint OfferSnapLayouts(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int NcHitTest = 0x0084, NcMouseLeave = 0x02A2, HtMaxButton = 9;
        if (message == NcMouseLeave)
        {
            MaximizeButton.Tag = null;
            return 0;
        }
        if (message != NcHitTest || !MaximizeButton.IsVisible)
        {
            return 0;
        }
        var cursor = new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
        Point topLeft = MaximizeButton.PointToScreen(new Point(0, 0));
        Point bottomRight = MaximizeButton.PointToScreen(new Point(MaximizeButton.ActualWidth, MaximizeButton.ActualHeight));
        bool isOver = cursor.X >= topLeft.X && cursor.X < bottomRight.X && cursor.Y >= topLeft.Y && cursor.Y < bottomRight.Y;
        MaximizeButton.Tag = isOver ? "hover" : null;
        if (!isOver)
        {
            return 0;
        }
        handled = true;
        return HtMaxButton;
    }

    /// <summary>
    /// A borderless window maximizes over the whole monitor, taskbar included, and hides its bottom edge.
    /// Answering WM_GETMINMAXINFO with the monitor's work area keeps it inside the usable space.
    /// </summary>
    private static nint LimitMaximizeToWorkArea(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int GetMinMaxInfo = 0x0024;
        const uint DefaultToNearest = 2;
        if (message != GetMinMaxInfo)
        {
            return 0;
        }
        nint monitor = NativeMethods.MonitorFromWindow(hwnd, DefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return 0;
        }
        var limits = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MinMaxInfo>(lParam);
        limits.MaxPosition = new NativeMethods.Point(info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
        limits.MaxSize = new NativeMethods.Point(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        System.Runtime.InteropServices.Marshal.StructureToPtr(limits, lParam, false);
        return 0;
    }

    private void OnSectionChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string section } || !IsInitialized || FonePanel is null)
        {
            return;
        }
        currentSection = section;
        ShowPanel(section);
        SetSettingsGear(isOpen: false);

        // Turn the ring the short way towards the clicked moon: one below the front turns it
        // counter-clockwise, one above turns it clockwise. The angle accumulates, so repeated clicks
        // keep spinning in the direction of the click instead of unwinding back.
        double onScreen = (MoonAngles[section] + ringAngle) % 360;
        onScreen = onScreen > 180 ? onScreen - 360 : onScreen <= -180 ? onScreen + 360 : onScreen;
        ringAngle -= onScreen;
        RingRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(ringAngle, OrbitTurn) { EasingFunction = EaseOut });
        foreach (FrameworkElement moon in new FrameworkElement[] { FoneMoon, MicMoon, EqMoon })
        {
            ((RotateTransform)moon.RenderTransform).BeginAnimation(
                RotateTransform.AngleProperty,
                new DoubleAnimation(-ringAngle, OrbitTurn) { EasingFunction = EaseOut });
        }

    }

    /// <summary>Shows one of the right-hand panels ("fone", "mic", "eq" or "settings") with a short slide-in.</summary>
    private void ShowPanel(string panel)
    {
        FonePanel.Visibility = panel == "fone" ? Visibility.Visible : Visibility.Collapsed;
        MicPanel.Visibility = panel == "mic" ? Visibility.Visible : Visibility.Collapsed;
        EqPanel.Visibility = panel == "eq" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = panel == "settings" ? Visibility.Visible : Visibility.Collapsed;
        SaveBar.Visibility = panel == "settings" ? Visibility.Collapsed : Visibility.Visible; // settings save themselves
        if (panel == "eq")
        {
            DrawEqualizerCurve();
        }
        Panels.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = EaseOut });
        PanelsShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = EaseOut });
    }

    /// <summary>
    /// ~4.2 s: horizon and plain appear, the cow nibbles the grass, the saucer arrives, beams it up and leaves.
    /// Skipped when Windows animations are off.
    /// </summary>
    private void PlaySplash()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            FinishSplash();
            return;
        }
        Animate(HorizonScale, ScaleTransform.ScaleXProperty, At(0, 0), At(0.7, 1, EaseOut));
        Animate(Ground, OpacityProperty, At(0.15, 0), At(0.65, 1));
        Animate(Cow, OpacityProperty, At(0.2, 0), At(0.6, 1), At(2.3, 1), At(3.3, 0.9), At(3.45, 0));
        Animate(Grass, OpacityProperty, At(0.3, 0), At(0.7, 1));

        // Nibbling: the cow leans towards the tuft four times while the tuft gets shorter.
        Animate(CowGraze, RotateTransform.AngleProperty, At(0.55, 0), At(1.0, 4, EaseInOut), At(1.45, 0, EaseInOut), At(1.9, 4, EaseInOut), At(2.35, 0, EaseInOut));
        Animate(GrassScale, ScaleTransform.ScaleYProperty, At(0.7, 1), At(1.3, 0.78), At(1.9, 0.56), At(2.5, 0.35));

        Animate(Saucer, OpacityProperty, At(1.25, 0), At(1.6, 1), At(3.55, 1), At(4.15, 0));
        Animate(SaucerShift, TranslateTransform.XProperty, At(1.25, 240), At(2.25, 0, EaseOut), At(3.55, 0), At(4.15, -300, EaseIn));
        Animate(SaucerShift, TranslateTransform.YProperty, At(1.25, -160), At(2.25, 0, EaseOut), At(2.8, -6, EaseInOut), At(3.35, 0, EaseInOut), At(3.55, 0), At(4.15, -220, EaseIn));
        Animate(SaucerSpin, RotateTransform.AngleProperty, At(1.25, 14), At(2.25, 0, EaseOut), At(3.55, 0), At(4.15, -10, EaseIn));
        Animate(SaucerScale, ScaleTransform.ScaleXProperty, At(3.55, 1), At(4.15, 0.55, EaseIn));
        Animate(SaucerScale, ScaleTransform.ScaleYProperty, At(3.55, 1), At(4.15, 0.55, EaseIn));

        Animate(Tractor, OpacityProperty, At(2.1, 0), At(2.36, 1), At(3.35, 1), At(3.55, 0));
        Animate(TractorScale, ScaleTransform.ScaleYProperty, At(2.1, 0), At(2.36, 1, EaseOut));

        // Abduction: a small hop, then up into the saucer, shrinking and turning.
        Animate(CowLift, TranslateTransform.YProperty, At(2.45, 0), At(2.65, -6, EaseInOut), At(3.45, -140, EaseInOut));
        Animate(CowSpin, RotateTransform.AngleProperty, At(2.45, 0), At(2.65, -4, EaseInOut), At(3.45, 8, EaseInOut));
        Animate(CowScale, ScaleTransform.ScaleXProperty, At(2.65, 1), At(3.45, 0.25, EaseInOut));
        Animate(CowScale, ScaleTransform.ScaleYProperty, At(2.65, 1), At(3.45, 0.25, EaseInOut));

        var fadeOut = Keys(At(4.2, 1), At(4.55, 0, EaseOut));
        fadeOut.Completed += (_, _) => FinishSplash();
        SplashOverlay.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void OnSplashClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => FinishSplash();

    private void FinishSplash()
    {
        if (isSplashDone)
        {
            return;
        }
        isSplashDone = true;
        SplashOverlay.BeginAnimation(OpacityProperty, null);
        SplashOverlay.Visibility = Visibility.Collapsed;
        Shell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)) { EasingFunction = EaseOut });
    }

    private readonly record struct Frame(double Seconds, double Value, IEasingFunction? Easing);

    private static Frame At(double seconds, double value, IEasingFunction? easing = null) => new(seconds, value, easing);

    private static void Animate(IAnimatable target, DependencyProperty property, params Frame[] frames) =>
        target.BeginAnimation(property, Keys(frames));

    private static DoubleAnimationUsingKeyFrames Keys(params Frame[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        // Hold the first value from time zero so nothing pops before its cue.
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(frames[0].Value, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        foreach (Frame frame in frames)
        {
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(frame.Value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(frame.Seconds)), frame.Easing));
        }
        return animation;
    }
}
