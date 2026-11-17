using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Ufo.Device;

namespace Ufo;

/// <summary>
/// Battery shown as the saucer's tractor beam: the charge fills the cone from the bottom, cut by thin
/// gaps in the page colour. The gaps drift down at rest, up and faster while charging, and slow down
/// (in red) when the battery is low.
/// </summary>
public partial class MainWindow
{
    private const double BeamHeight = 150;
    private const double GapPitch = 16;
    private const int LowBatteryPercent = 20;

    private const double UnitPixels = 118.0 / 200; // the saucer's 200-unit artboard is drawn 118 px wide
    private const double FallUnits = 127; // from the hover point down to where the saucer lodges
    private const double Impact = 0.648; // seconds into the fall
    private static readonly Color DeadLight = Color.FromRgb(0x4A, 0x4A, 0x45);
    private static readonly Color FaintLight = Color.FromRgb(0x9A, 0x4A, 0x2A);

    private enum BeamMood { Resting, Charging, Low }

    private BeamMood? beamMood;
    private bool hasBeamEntered;
    private bool? isUfoCrashed;
    private bool hasCrashLoops;
    private SolidColorBrush? crashLight;

    private void InitializeBeam()
    {
        for (int index = 0; index < 12; index++)
        {
            var gap = new Rectangle { Width = 200, Height = 4 };
            gap.SetResourceReference(Shape.FillProperty, "BgBrush"); // follows the background theme
            System.Windows.Controls.Canvas.SetTop(gap, index * GapPitch - GapPitch);
            BeamGaps.Children.Add(gap);
        }
    }

    /// <summary>The saucer and its beam hover together in the middle of the orbit, all the time.</summary>
    private void StartFloating()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }
        UfoFloat.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -6, TimeSpan.FromSeconds(1.4))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    /// <summary>
    /// Headset offline: the power dies (beam back into the hatch, lights stuttering out), the saucer hangs
    /// for a beat, drops like a stone and rocks once where it lodges. Back online it wiggles loose, shoots up
    /// past the hover point and settles, and only then the beam comes back. One saucer plays both ways: the
    /// crashed one, which starts or ends exactly over the flying one. Prototype: prototypes/ufo-crash.html.
    /// </summary>
    private void ShowCrash(bool isCrashed)
    {
        if (isCrashed == isUfoCrashed)
        {
            return; // called on every battery poll; only a change of state plays
        }
        bool isFirstReport = isUfoCrashed is null;
        isUfoCrashed = isCrashed;
        if (crashLight is null)
        {
            crashLight = new SolidColorBrush(DeadLight); // the crashed saucer's own light brush
            CrashedSaucer.Resources["UfoLightBrush"] = crashLight;
        }
        if (!SystemParameters.ClientAreaAnimation || (isFirstReport && !isCrashed))
        {
            FlyingUfo.Visibility = isCrashed ? Visibility.Hidden : Visibility.Visible; // Hidden keeps the layout height
            CrashedUfo.Visibility = isCrashed ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        Color lit = ((SolidColorBrush)FindResource("UfoLightBrush")).Color;
        SolidColorBrush light = crashLight;
        FlyingUfo.Visibility = Visibility.Visible; // for the beam; the crashed saucer stands in for the flying one
        FlyingSaucer.Visibility = Visibility.Hidden;
        CrashedUfo.Visibility = Visibility.Visible;
        if (isCrashed)
        {
            // Start where the bobbing saucer is right now, and let the bob die out under the beam.
            double hover = -FallUnits + UfoFloat.Y / UnitPixels;
            UfoFloat.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(0.2)));
            Animate(BeamRetract, ScaleTransform.ScaleYProperty, At(0, 1), At(0.2, 0, EaseIn));
            Animate(CrashSlamShift, TranslateTransform.YProperty,
                At(0, hover), At(0.108, -FallUnits + 2, EaseOut), At(0.18, -FallUnits - 1), At(0.27, -FallUnits - 5), At(Impact, 0, EaseIn));
            Animate(CrashTilt, RotateTransform.AngleProperty,
                At(0, 0), At(0.108, -3, EaseOut), At(0.18, 3), At(0.27, -5), At(Impact, -66, EaseIn), At(0.774, -55, EaseOut), At(0.9, -58, EaseInOut));
            Animate(CrashGround, OpacityProperty, At(0, 0), At(0.2, 1, EaseOut));
            Animate(CrashDebris, OpacityProperty, At(Impact - 0.05, 0), At(Impact + 0.17, 1, EaseOut));
            Animate(CrashDebrisScale, ScaleTransform.ScaleXProperty, At(Impact - 0.05, 0.6), At(Impact + 0.17, 1, EaseOut));
            Animate(CrashDebrisScale, ScaleTransform.ScaleYProperty, At(Impact - 0.05, 0.6), At(Impact + 0.17, 1, EaseOut));
            Animate(CrashWounds, OpacityProperty, At(Impact, 0), At(Impact + 0.1, 1));
            FadeReadoutIn(Impact);

            ColorAnimationUsingKeyFrames lightsOut = Steps(0.3, (0, lit), (0.075, DeadLight), (0.12, lit), (0.165, DeadLight), (0.21, FaintLight), (0.3, DeadLight));
            lightsOut.Completed += (_, _) =>
            {
                if (isUfoCrashed != true)
                {
                    return;
                }
                // Lying there: dead, with a failing flicker now and then.
                ColorAnimationUsingKeyFrames flicker = Steps(3.6, (0, DeadLight), (0.9, lit), (0.96, DeadLight), (1.06, lit), (1.2, FaintLight),
                    (1.28, DeadLight), (2.7, FaintLight), (2.76, DeadLight), (2.84, lit), (2.9, DeadLight));
                flicker.RepeatBehavior = RepeatBehavior.Forever;
                light.BeginAnimation(SolidColorBrush.ColorProperty, flicker);
            };
            light.BeginAnimation(SolidColorBrush.ColorProperty, lightsOut);

            if (!hasCrashLoops)
            {
                hasCrashLoops = true; // sparks and the sputtering beam loop for good; CrashWounds hides them in flight
                Blink(CrashSparkTop, 2.8, 0, (1.74, 1), (1.79, 0), (1.85, 1), (1.93, 0));
                Blink(CrashSparkBelly, 2.8, 1.3, (1.74, 1), (1.79, 0), (1.85, 1), (1.93, 0));
                Blink(CrashBeam, 4.4, 0, (1.8, 0.5), (1.89, 0), (1.98, 0.7), (2.2, 0.25), (2.29, 0));
            }
            return;
        }

        DoubleAnimationUsingKeyFrames rise = Keys(At(0, 0), At(0.28, -2), At(0.7, -FallUnits - 9, EaseInOut), At(1.0, -FallUnits, EaseInOut));
        rise.Completed += (_, _) =>
        {
            if (isUfoCrashed != false)
            {
                return;
            }
            FlyingSaucer.Visibility = Visibility.Visible; // hand back to the flying saucer, in the same place
            CrashedUfo.Visibility = Visibility.Collapsed;
            StartFloating();
        };
        CrashSlamShift.BeginAnimation(TranslateTransform.YProperty, rise);
        Animate(CrashTilt, RotateTransform.AngleProperty,
            At(0, -58), At(0.1, -52), At(0.2, -61), At(0.28, -56), At(0.7, 5, EaseInOut), At(1.0, 0, EaseInOut));
        Animate(CrashWounds, OpacityProperty, At(0, 0));
        Animate(CrashGround, OpacityProperty, At(0.2, 1), At(0.45, 0));
        Animate(CrashDebris, OpacityProperty, At(0.2, 1), At(0.45, 0));
        Animate(CrashDebrisScale, ScaleTransform.ScaleXProperty, At(0.2, 1), At(0.45, 0.7));
        Animate(CrashDebrisScale, ScaleTransform.ScaleYProperty, At(0.2, 1), At(0.45, 0.7));
        Animate(BeamRetract, ScaleTransform.ScaleYProperty, At(0.65, 0), At(1.0, 1, EaseOut));
        Animate(BeamLevelScale, ScaleTransform.ScaleYProperty, At(0.8, 0), At(1.4, 1, EaseOut));
        FadeReadoutIn(0.7);
        light.BeginAnimation(SolidColorBrush.ColorProperty, Steps(0.3, (0, DeadLight), (0.06, lit), (0.105, DeadLight), (0.15, lit)));
    }

    /// <summary>The percentage and the status line change with the saucer: gone at once, back at <paramref name="seconds"/>.</summary>
    private void FadeReadoutIn(double seconds)
    {
        Animate(BatteryText, OpacityProperty, At(seconds, 0), At(seconds + 0.15, 1, EaseOut));
        Animate(LinkText, OpacityProperty, At(seconds, 0), At(seconds + 0.15, 1, EaseOut));
    }

    /// <summary>Hard colour steps, holding the last one.</summary>
    private static ColorAnimationUsingKeyFrames Steps(double seconds, params (double Seconds, Color Color)[] steps)
    {
        var animation = new ColorAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(seconds) };
        foreach ((double second, Color color) in steps)
        {
            animation.KeyFrames.Add(new DiscreteColorKeyFrame(color, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(second))));
        }
        return animation;
    }

    /// <summary>Loops hard on/off opacity steps, starting hidden; <paramref name="offsetSeconds"/> shifts the loop's phase.</summary>
    private static void Blink(UIElement target, double loopSeconds, double offsetSeconds, params (double Seconds, double Opacity)[] steps)
    {
        var blink = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(loopSeconds),
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(-offsetSeconds),
        };
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        foreach ((double seconds, double opacity) in steps)
        {
            blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(opacity, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds))));
        }
        target.BeginAnimation(OpacityProperty, blink);
    }

    private void ShowBeam(BatteryState? battery)
    {
        int level = battery?.Level ?? 0;
        double levelY = BeamHeight - BeamHeight * Math.Clamp(level, 0, 100) / 100;
        BeamLevelClip.Rect = new Rect(0, levelY, 200, BeamHeight - levelY);

        BeamMood mood = battery is null ? BeamMood.Resting
            : battery.IsCharging ? BeamMood.Charging
            : level <= LowBatteryPercent ? BeamMood.Low
            : BeamMood.Resting;
        BeamCharge.SetResourceReference(Shape.FillProperty, mood == BeamMood.Low ? "LowBrush" : "AccentBrush");
        if (mood != beamMood)
        {
            beamMood = mood;
            (double to, double seconds) = mood switch
            {
                BeamMood.Charging => (-GapPitch, 1.2),
                BeamMood.Low => (GapPitch, 4.0),
                _ => (GapPitch, 2.4),
            };
            BeamGapsShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, to, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        if (!hasBeamEntered && battery is not null)
        {
            hasBeamEntered = true;
            BeamLevelScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)) { EasingFunction = EaseOut });
        }
    }
}
