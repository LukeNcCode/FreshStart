using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using FreshStart.App.Animation;
using FreshStart.Core.UI;

namespace FreshStart.App.Startup;

/// <summary>
/// 首次启动的电影式品牌开场：完整描边 → 白色填充扫过 → 轻微放大淡出。
/// 动画结束后直接揭示主窗口的开荒工作台。
/// </summary>
public partial class StartupIntroView : UserControl
{
    private bool _started;

    public StartupIntroView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_started) return;
            _started = true;
            await PlayAsync();
        };
    }

    public event EventHandler? Completed;

    private async Task PlayAsync()
    {
        if (MotionPreferences.Duration(1) <= TimeSpan.Zero)
        {
            Completed?.Invoke(this, EventArgs.Empty);
            return;
        }

        PrepareWordGeometry();
        var easing = new CubicBezierEase();

        OutlineWord.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        await Task.Delay(210);

        var bounds = OutlineWord.Data?.Bounds ?? Rect.Empty;
        var empty = new Rect(0, 0, 0, Math.Max(1, bounds.Height));
        var full = new Rect(0, 0, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        FillClip.Rect = empty;
        FillClip.BeginAnimation(
            RectangleGeometry.RectProperty,
            new RectAnimation(empty, full, new Duration(TimeSpan.FromMilliseconds(1120)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            });
        FillBlur.BeginAnimation(
            BlurEffect.RadiusProperty,
            new DoubleAnimation(0.9, 0.18, new Duration(TimeSpan.FromMilliseconds(1120)))
            {
                EasingFunction = easing
            });
        await Task.Delay(1190);

        var exitDuration = MotionPreferences.Duration(360);
        var fade = new DoubleAnimation(1, 0, new Duration(exitDuration))
        {
            BeginTime = TimeSpan.FromMilliseconds(60),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => Completed?.Invoke(this, EventArgs.Empty);
        LayoutRoot.BeginAnimation(OpacityProperty, fade);
        FrameScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 1.055, new Duration(exitDuration))
            {
                BeginTime = TimeSpan.FromMilliseconds(60),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            });
        FrameScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 1.055, new Duration(exitDuration))
            {
                BeginTime = TimeSpan.FromMilliseconds(60),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            });
        FillBlur.BeginAnimation(
            BlurEffect.RadiusProperty,
            new DoubleAnimation(0.18, 2.6, new Duration(exitDuration))
            {
                BeginTime = TimeSpan.FromMilliseconds(60),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            });
    }

    private void PrepareWordGeometry()
    {
        var family = TryFindResource("DisplayFont") as FontFamily
                     ?? new FontFamily("Bahnschrift SemiBold");
        var typeface = new Typeface(
            family,
            FontStyles.Normal,
            FontWeights.SemiBold,
            FontStretches.Normal);
        var text = new FormattedText(
            "FreshStart",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            142,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var geometry = text.BuildGeometry(new Point(0, 0));
        OutlineWord.Data = geometry;
        FillWord.Data = geometry;
    }
}
