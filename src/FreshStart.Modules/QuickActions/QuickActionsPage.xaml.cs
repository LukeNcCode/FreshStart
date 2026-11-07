using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using FreshStart.Core;
using FreshStart.Core.UI;
using FreshStart.Modules.Controls;

namespace FreshStart.Modules.QuickActions;

public partial class QuickActionsPage
{
    private CancellationTokenSource? _scoreAnimationCts;

    public QuickActionsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not QuickActionsViewModel vm) return;

        _scoreAnimationCts?.Cancel();
        _scoreAnimationCts?.Dispose();
        var cts = new CancellationTokenSource();
        _scoreAnimationCts = cts;

        ReadinessRing.BeginAnimation(ScoreRing.ValueProperty, null);
        ReadinessRing.Value = 0;
        ScoreNumber.Text = "0";

        await vm.LoadScoreAsync();
        if (cts.IsCancellationRequested || !IsLoaded) return;

        await AnimateScoreAsync(vm.ScorePercent, cts.Token);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _scoreAnimationCts?.Cancel();
        _scoreAnimationCts?.Dispose();
        _scoreAnimationCts = null;
    }

    private async Task AnimateScoreAsync(double target, CancellationToken ct)
    {
        var safeTarget = Math.Clamp(target, 0, 100);
        var duration = MotionPreferences.Duration(950);
        if (duration <= TimeSpan.Zero)
        {
            ReadinessRing.Value = safeTarget;
            ScoreNumber.Text = $"{safeTarget:0}";
            return;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        Log.Info($"首页评分动画开始: 0 → {safeTarget:0}");
        var ringAnimation = new DoubleAnimation(0, safeTarget, new Duration(duration))
        {
            EasingFunction = easing
        };
        ReadinessRing.BeginAnimation(ScoreRing.ValueProperty, ringAnimation);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (stopwatch.Elapsed < duration)
            {
                var progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
                var eased = 1 - Math.Pow(1 - progress, 3);
                ScoreNumber.Text = $"{safeTarget * eased:0}";
                await Task.Delay(16, ct);
            }

            ReadinessRing.BeginAnimation(ScoreRing.ValueProperty, null);
            ReadinessRing.Value = safeTarget;
            ScoreNumber.Text = $"{safeTarget:0}";
            Log.Info($"首页评分动画完成: {safeTarget:0}");
        }
        catch (OperationCanceledException)
        {
            // 离开页面时停止动画，下一次进入会重新从 0 开始。
        }
    }
}
