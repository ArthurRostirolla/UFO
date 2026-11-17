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

    private enum BeamMood { Resting, Charging, Low }

    private BeamMood? beamMood;
    private bool hasBeamEntered;
    private bool? isUfoCrashed;

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
    /// Swaps the flying saucer for the crashed one while the headset is offline. The crashed saucer gets its
    /// own light brush: dead, with a failing flicker now and then. Each crash slams in once; the sparks and
    /// the sputtering beam loop while it lies there.
    /// </summary>
    private void ShowCrash(bool isCrashed)
    {
        if (isCrashed == isUfoCrashed)
        {
            return; // called on every battery poll; only a change of state replays the impact
        }
        isUfoCrashed = isCrashed;
        FlyingUfo.Visibility = isCrashed ? Visibility.Hidden : Visibility.Visible; // Hidden keeps the layout height
        CrashedUfo.Visibility = isCrashed ? Visibility.Visible : Visibility.Collapsed;
        if (!isCrashed)
        {
            return;
        }
        if (SystemParameters.ClientAreaAnimation)
        {
            Animate(CrashSlam, OpacityProperty, At(0, 0), At(0.26, 1, EaseOut));
            Animate(CrashSlamShift, TranslateTransform.XProperty, At(0, 46), At(0.26, 0, EaseOut));
            Animate(CrashSlamShift, TranslateTransform.YProperty, At(0, -74), At(0.26, 0, EaseOut));
            Animate(CrashDebris, OpacityProperty, At(0.18, 0), At(0.4, 1, EaseOut));
            Animate(CrashDebrisScale, ScaleTransform.ScaleXProperty, At(0.18, 0.6), At(0.4, 1, EaseOut));
            Animate(CrashDebrisScale, ScaleTransform.ScaleYProperty, At(0.18, 0.6), At(0.4, 1, EaseOut));
        }
        if (CrashedSaucer.Resources.Contains("UfoLightBrush"))
        {
            return; // the loops below are set up once
        }
        Color lit = ((SolidColorBrush)FindResource("UfoLightBrush")).Color;
        Color dead = Color.FromRgb(0x4A, 0x4A, 0x45);
        Color faint = Color.FromRgb(0x9A, 0x4A, 0x2A);
        var light = new SolidColorBrush(dead);
        CrashedSaucer.Resources["UfoLightBrush"] = light;
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }
        var flicker = new ColorAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(3.6), RepeatBehavior = RepeatBehavior.Forever };
        foreach ((double second, Color color) in new[]
        {
            (0.0, dead), (0.9, lit), (0.96, dead), (1.06, lit), (1.2, faint), (1.28, dead),
            (2.7, faint), (2.76, dead), (2.84, lit), (2.9, dead),
        })
        {
            flicker.KeyFrames.Add(new DiscreteColorKeyFrame(color, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(second))));
        }
        light.BeginAnimation(SolidColorBrush.ColorProperty, flicker);

        Blink(CrashSparkTop, 2.8, 0, (1.74, 1), (1.79, 0), (1.85, 1), (1.93, 0));
        Blink(CrashSparkBelly, 2.8, 1.3, (1.74, 1), (1.79, 0), (1.85, 1), (1.93, 0));
        Blink(CrashBeam, 4.4, 0, (1.8, 0.5), (1.89, 0), (1.98, 0.7), (2.2, 0.25), (2.29, 0));
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
