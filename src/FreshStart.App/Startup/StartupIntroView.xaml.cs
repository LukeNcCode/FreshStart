using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FreshStart.App.Animation;
using FreshStart.Core;
using FreshStart.Core.UI;

namespace FreshStart.App.Startup;

/// <summary>
/// 首次启动的电影式品牌开场：完整描边 → 白色填充扫过 → 黑白反转 → 淡出。
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
    public event EventHandler? ExitStarted;
    public bool HasMotion => MotionPreferences.AnimationsEnabled;
    public Func<Rect?>? TargetWordmarkProvider { get; set; }

    private async Task PlayAsync()
    {
        if (MotionPreferences.Duration(1) <= TimeSpan.Zero)
        {
            ExitStarted?.Invoke(this, EventArgs.Empty);
            Completed?.Invoke(this, EventArgs.Empty);
            return;
        }

        PrepareWordGeometry();
        var outlineEase = new CubicBezierEase(0, 0, 0.58, 1);
        var fillEase = new CubicBezierEase(0.65, 0, 0.35, 1);
        var invertEase = new CubicBezierEase(0.65, 0, 0.35, 1);

        OutlineWord.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(400)))
            {
                EasingFunction = outlineEase
            });
        await Task.Delay(400);

        var bounds = OutlineWord.Data?.Bounds ?? Rect.Empty;
        var empty = new Rect(bounds.X, bounds.Y, 0, Math.Max(1, bounds.Height));
        var full = new Rect(
            bounds.X,
            bounds.Y,
            Math.Max(1, bounds.Width),
            Math.Max(1, bounds.Height));
        FillClip.Rect = empty;
        FillClip.BeginAnimation(
            RectangleGeometry.RectProperty,
            new RectAnimation(empty, full, new Duration(TimeSpan.FromMilliseconds(2000)))
            {
                EasingFunction = fillEase
            });
        await Task.Delay(2000);
        await Task.Delay(200);

        // 白色反相层从右向左扫过，形成清晰的黑白分界。
        var width = Math.Max(1, LayoutRoot.ActualWidth);
        var height = Math.Max(1, LayoutRoot.ActualHeight);
        var hidden = new Rect(width, 0, 0, height);
        var revealed = new Rect(0, 0, width, height);
        InvertClip.Rect = hidden;
        InvertClip.BeginAnimation(
            RectangleGeometry.RectProperty,
            new RectAnimation(hidden, revealed, new Duration(TimeSpan.FromMilliseconds(750)))
            {
                EasingFunction = invertEase
            });
        await Task.Delay(750);
        await Task.Delay(250);

        ExitStarted?.Invoke(this, EventArgs.Empty);
        var target = TargetWordmarkProvider?.Invoke();
        if (target is { IsEmpty: false, Width: > 1, Height: > 1 })
        {
            await MoveWordToBrandAsync(target.Value);
        }

        // 反相层已完整覆盖；先移除黑底与原层，只降低白色背景。
        Background = Brushes.Transparent;
        LayoutRoot.Background = Brushes.Transparent;
        NormalLayer.Visibility = Visibility.Collapsed;
        var exitDuration = MotionPreferences.Duration(400);
        var fade = new DoubleAnimation(1, 0, new Duration(exitDuration))
        {
            EasingFunction = new CubicBezierEase(0, 0, 0.58, 1)
        };
        InvertBackdrop.BeginAnimation(OpacityProperty, fade);
        await Task.Delay(exitDuration);
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private async Task MoveWordToBrandAsync(Rect target)
    {
        var localBounds = InvertWord.Data?.Bounds ?? new Rect(InvertWord.RenderSize);
        var current = InvertWord.TransformToAncestor(this)
            .TransformBounds(localBounds);
        if (current.Width <= 1 || current.Height <= 1) return;

        var center = new Point(
            current.Left + current.Width / 2,
            current.Top + current.Height / 2);
        var targetCenter = new Point(
            target.Left + target.Width / 2,
            target.Top + target.Height / 2);
        var scaleX = target.Width / current.Width;
        var scaleY = target.Height / current.Height;
        Log.Info(
            $"启动动画字标缩放: current={current.Width:0.##}x{current.Height:0.##}, " +
            $"target={target.Width:0.##}x{target.Height:0.##}, " +
            $"scale={scaleX:0.###}x{scaleY:0.###}");
        InvertWordLayer.RenderTransformOrigin = new Point(
            center.X / Math.Max(1, ActualWidth),
            center.Y / Math.Max(1, ActualHeight));

        var duration = MotionPreferences.Duration(700);
        var ease = new CubicBezierEase(0.65, 0, 0.35, 1);
        InvertWordScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, scaleX, new Duration(duration))
            {
                EasingFunction = ease
            });
        InvertWordScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, scaleY, new Duration(duration))
            {
                EasingFunction = ease
            });
        InvertWordTranslate.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(0, targetCenter.X - center.X, new Duration(duration))
            {
                EasingFunction = ease
            });
        InvertWordTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, targetCenter.Y - center.Y, new Duration(duration))
            {
                EasingFunction = ease
            });
        await Task.Delay(duration);
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
        InvertWord.Data = geometry;
    }
}
