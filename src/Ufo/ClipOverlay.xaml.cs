using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ufo;

/// <summary>
/// One pixel in a monitor's corner that changes (invisibly: black to almost black) ten times a second.
/// Windows only hands the screen recorder a new frame when the screen changes, so on a still screen the
/// recording would stop advancing; this keeps it moving. Click-through and never focused.
/// </summary>
public sealed class ClipTicker : Window
{
    private static readonly Brush Off = Brushes.Black, On = new SolidColorBrush(Color.FromRgb(1, 1, 1));
    private readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly System.Drawing.Point corner;

    public ClipTicker(System.Drawing.Rectangle monitor)
    {
        corner = monitor.Location;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        Width = Height = 1;
        Background = Off;
        timer.Tick += (_, _) => Background = Background == Off ? On : Off;
        Closed += (_, _) => timer.Stop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        nint hwnd = new WindowInteropHelper(this).Handle;
        nint style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.ExtendedStyle);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.ExtendedStyle, style | NativeMethods.Transparent | NativeMethods.ToolWindow | NativeMethods.NoActivate);
        const uint NoZOrder = 0x4, NoActivate = 0x10;
        NativeMethods.SetWindowPos(hwnd, 0, corner.X, corner.Y, 1, 1, NoZOrder | NoActivate);
        timer.Start();
    }
}

/// <summary>
/// The on-screen notice of the clips: click-through, never focused, always on top and excluded from
/// screen capture, so it shows over a (borderless) game without ending up inside the clip.
/// </summary>
public partial class ClipOverlay : Window
{
    private const int EdgeGap = 24;
    private System.Drawing.Rectangle area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? default;
    private int corner = 1;
    private bool isToasting;

    public ClipOverlay()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Place();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        nint hwnd = new WindowInteropHelper(this).Handle;
        nint style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.ExtendedStyle);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.ExtendedStyle, style | NativeMethods.Transparent | NativeMethods.ToolWindow | NativeMethods.NoActivate);
        NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.ExcludeFromCapture);
    }

    /// <summary>Which corner (0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right) of which screen area, in pixels.</summary>
    public void MoveTo(int newCorner, System.Drawing.Rectangle workingArea)
    {
        corner = newCorner;
        area = workingArea;
        Dot.HorizontalAlignment = corner % 2 == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Dot.VerticalAlignment = corner < 2 ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        Place();
    }

    /// <summary>The small dot that says the replay buffer is running.</summary>
    public void ShowDot(bool isVisible)
    {
        Dot.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        if (isVisible)
        {
            Show();
            Place();
        }
        else if (!isToasting)
        {
            Hide();
        }
    }

    /// <summary>Slides the plate in, drops the beam on the caption, and slides out (about three seconds).</summary>
    public void Toast(string title, string meta)
    {
        TitleText.Text = title;
        MetaText.Text = meta;
        isToasting = true;
        Show();
        Place();
        int away = corner < 2 ? -14 : 14; // leaves towards the nearest edge
        bool isMoving = SystemParameters.ClientAreaAnimation;
        DoubleAnimationUsingKeyFrames fade = isMoving
            ? MainWindow.Keys(MainWindow.At(0, 0), MainWindow.At(0.3, 1, MainWindow.EaseOut), MainWindow.At(2.6, 1), MainWindow.At(2.95, 0, MainWindow.EaseIn))
            : MainWindow.Keys(MainWindow.At(0, 1), MainWindow.At(2.6, 1), MainWindow.At(2.61, 0));
        fade.Completed += (_, _) =>
        {
            isToasting = false;
            if (Dot.Visibility != Visibility.Visible)
            {
                Hide();
            }
        };
        Plate.BeginAnimation(OpacityProperty, fade);
        if (isMoving)
        {
            MainWindow.Animate(PlateShift, TranslateTransform.YProperty,
                MainWindow.At(0, away), MainWindow.At(0.3, 0, MainWindow.EaseOut), MainWindow.At(2.6, 0), MainWindow.At(2.95, away, MainWindow.EaseIn));
            MainWindow.Animate(BeamScale, ScaleTransform.ScaleYProperty,
                MainWindow.At(0.25, 0), MainWindow.At(0.6, 1, MainWindow.EaseOut), MainWindow.At(1.1, 1), MainWindow.At(1.4, 0, MainWindow.EaseIn));
        }
    }

    /// <summary>Positions the window in physical pixels, so it lands right on monitors of any scale.</summary>
    private void Place()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0)
        {
            return;
        }
        const uint NoSize = 0x1, NoZOrder = 0x4, NoActivate = 0x10;
        DpiScale scale = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(ActualWidth * scale.DpiScaleX), height = (int)Math.Ceiling(ActualHeight * scale.DpiScaleY);
        int x = corner % 2 == 0 ? area.Left + EdgeGap : area.Right - EdgeGap - width;
        int y = corner < 2 ? area.Top + EdgeGap : area.Bottom - EdgeGap - height;
        NativeMethods.SetWindowPos(hwnd, 0, x, y, 0, 0, NoSize | NoZOrder | NoActivate);
    }
}
