using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FreshStart.App.Animation;
using FreshStart.App.Dialogs;
using FreshStart.Core;
using FreshStart.Core.UI;

namespace FreshStart.App.Controls;

/// <summary>
/// 自绘模态对话框（非系统 MessageBox）。同一时刻只允许一个弹窗，避免嵌套确认。
/// </summary>
public partial class ModalHost
{
    private TaskCompletionSource<DialogResult>? _completion;
    private CancellationTokenRegistration _registration;

    public ModalHost()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public Task<DialogResult> ShowAsync(DialogRequest request, CancellationToken ct = default)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.InvokeAsync(() => ShowAsync(request, ct), DispatcherPriority.Normal).Task.Unwrap();
        }

        if (_completion is not null)
        {
            Log.Warn("已有确认弹窗打开，新的确认请求被自动取消");
            return Task.FromResult(new DialogResult(null));
        }

        DataContext = request;
        Visibility = Visibility.Visible;
        _completion = new TaskCompletionSource<DialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (ct.CanBeCanceled)
        {
            _registration = ct.Register(() => Dispatcher.BeginInvoke(new Action(() => Complete(null))));
        }

        AnimateIn();
        Dispatcher.BeginInvoke(() => Keyboard.Focus(this), DispatcherPriority.Input);
        return _completion.Task;
    }

    public void Cancel() => Complete(null);

    public void ConfirmPrimary()
    {
        if (DataContext is DialogRequest request)
        {
            var primary = request.Actions.FirstOrDefault(action => action.IsPrimary);
            if (primary is not null) Complete(primary.Key);
        }
    }

    private void Complete(string? key)
    {
        if (_completion is null) return;
        var completion = _completion;
        _completion = null;
        _registration.Dispose();
        Visibility = Visibility.Collapsed;
        DataContext = null;
        completion.TrySetResult(new DialogResult(key));
    }

    private void AnimateIn()
    {
        var duration = MotionPreferences.Duration(200);
        if (duration <= TimeSpan.Zero)
        {
            Card.Opacity = 1;
            Card.RenderTransform = Transform.Identity;
            return;
        }

        Card.Opacity = 0;
        var scale = new ScaleTransform(0.98, 0.98);
        Card.RenderTransform = scale;
        var easing = new CubicBezierEase();

        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(duration)) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.98, 1, new Duration(duration)) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.98, 1, new Duration(duration)) { EasingFunction = easing });
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DialogAction action })
        {
            Complete(action.Key);
        }
    }

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 点击遮罩不再取消：无人值守预检后弹出确认时，误点背景会造成「无辜取消」。
        // 需要取消请点击「取消」按钮或按 Esc。
        Nudge();
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Complete(null);
        e.Handled = true;
    }

    /// <summary>轻微横向抖动，提示「请点击按钮」而不是悄悄什么都不做。</summary>
    private void Nudge()
    {
        var duration = MotionPreferences.Duration(240);
        if (duration <= TimeSpan.Zero) return;

        var transform = new TranslateTransform();
        Card.RenderTransform = transform;
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(duration)
        };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(-7, KeyTime.FromPercent(0.25)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(6, KeyTime.FromPercent(0.55)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(-3, KeyTime.FromPercent(0.8)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        transform.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }
}
