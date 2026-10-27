using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinDock.Services;

public static class DesktopMotion
{
    public const int EntranceMilliseconds = 160;
    public const int LaunchMilliseconds = 360;
    public static void Enter(FrameworkElement content)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var shift = new TranslateTransform(); content.RenderTransform = shift;
        var duration = TimeSpan.FromMilliseconds(EntranceMilliseconds);
        content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { FillBehavior = FillBehavior.Stop });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, duration)
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
    /// <summary>A single lift and a small settling rebound. Repeated clicks replace the previous clock.</summary>
    public static void Launch(TranslateTransform shift, double iconSize, double strength)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var lift = -Math.Clamp(iconSize * strength, 6, 18);
        var motion = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(LaunchMilliseconds), FillBehavior = FillBehavior.Stop };
        motion.KeyFrames.Add(new EasingDoubleKeyFrame(shift.Y, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        motion.KeyFrames.Add(new EasingDoubleKeyFrame(lift, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        motion.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250)), new QuadraticEase { EasingMode = EasingMode.EaseInOut }));
        motion.KeyFrames.Add(new EasingDoubleKeyFrame(lift * 0.12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        motion.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(LaunchMilliseconds)), new QuadraticEase { EasingMode = EasingMode.EaseInOut }));
        shift.BeginAnimation(TranslateTransform.YProperty, motion, HandoffBehavior.SnapshotAndReplace);
    }
}
