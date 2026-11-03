namespace FreshStart.Core.UI;

/// <summary>
/// 全局动效偏好。设置页切换后立即生效，页面/控件动画都读取这里。
/// 放在 Core 是为了让模块（Modules）也能遵守无障碍设置，而不依赖 App。
/// </summary>
public static class MotionPreferences
{
    public static bool AnimationsEnabled { get; private set; } = true;

    public static void SetEnabled(bool enabled) => AnimationsEnabled = enabled;

    /// <summary>返回实际动画时长；关闭动效时为 0。</summary>
    public static TimeSpan Duration(int milliseconds)
        => AnimationsEnabled ? TimeSpan.FromMilliseconds(milliseconds) : TimeSpan.Zero;
}
