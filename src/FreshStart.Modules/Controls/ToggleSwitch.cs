using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FreshStart.Core.UI;

namespace FreshStart.Modules.Controls;

/// <summary>
/// 复刻 iOS 开关：44×26 轨道、22 白色滑块、180ms 位移。
/// 动效时长读取 MotionPreferences，关闭动效时立即切换。
/// </summary>
public class ToggleSwitch : Control
{
    private const double Travel = 18;
    private FrameworkElement? _thumb;
    private Border? _accent;

    static ToggleSwitch()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ToggleSwitch),
            new FrameworkPropertyMetadata(typeof(ToggleSwitch)));
        FocusableProperty.OverrideMetadata(typeof(ToggleSwitch), new FrameworkPropertyMetadata(true));
    }

    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(
        nameof(IsChecked), typeof(bool), typeof(ToggleSwitch),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsCheckedChanged));

    public bool IsChecked
    {
        get => (bool)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _thumb = GetTemplateChild("PART_Thumb") as FrameworkElement;
        _accent = GetTemplateChild("PART_Accent") as Border;

        // 模板里的 Freezable 会被 WPF 冻结，必须换成一个可写的 Transform
        if (_thumb is not null &&
            (_thumb.RenderTransform is not TranslateTransform transform || transform.IsFrozen))
        {
            _thumb.RenderTransform = new TranslateTransform();
        }

        UpdateVisual(animate: false);
    }

    private static void OnIsCheckedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ToggleSwitch)d).UpdateVisual(animate: true);

    private TranslateTransform EnsureTransform()
    {
        if (_thumb is null) return new TranslateTransform();
        if (_thumb.RenderTransform is TranslateTransform transform && !transform.IsFrozen) return transform;

        var created = new TranslateTransform();
        _thumb.RenderTransform = created;
        return created;
    }

    private void UpdateVisual(bool animate)
    {
        if (_thumb is null) return;
        var transform = EnsureTransform();
        var targetX = IsChecked ? Travel : 0;
        var targetOpacity = IsChecked ? 1d : 0d;
        var duration = MotionPreferences.Duration(180);

        if (!animate || duration <= TimeSpan.Zero)
        {
            transform.X = targetX;
            if (_accent is not null) _accent.Opacity = targetOpacity;
            return;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(transform.X, targetX, new Duration(duration)) { EasingFunction = easing });
        _accent?.BeginAnimation(OpacityProperty,
            new DoubleAnimation(_accent.Opacity, targetOpacity, new Duration(duration)) { EasingFunction = easing });
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseOver && IsEnabled)
        {
            IsChecked = !IsChecked;
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Space or Key.Enter)
        {
            IsChecked = !IsChecked;
            e.Handled = true;
        }
    }
}
