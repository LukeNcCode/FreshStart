namespace FreshStart.Core.Abstractions;

public enum ToastKind { Info, Success, Warning, Error }

/// <summary>右下角轻量提示，3 秒自动消失。</summary>
public interface IToastService
{
    void Show(string message, ToastKind kind = ToastKind.Info);
}
public sealed class NavigationChangedEventArgs : EventArgs
{
    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public object? ViewModel { get; init; }
}

/// <summary>页面导航。找不到或未实现的模块返回 false，由调用方提示。</summary>
public interface INavigationService
{
    string CurrentKey { get; }
    event EventHandler<NavigationChangedEventArgs>? Navigated;
    bool Navigate(string key);
}

public sealed class UserPrompt
{
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public IReadOnlyList<string> Details { get; set; } = Array.Empty<string>();
    public string ConfirmText { get; set; } = "继续";
    public string CancelText { get; set; } = "取消";
    public bool IsDangerous { get; set; }
}

/// <summary>普通二选一确认，由 UI 层实现自绘模态。</summary>
public interface IUserPromptService
{
    Task<bool> ConfirmAsync(UserPrompt prompt, CancellationToken ct = default);
}

/// <summary>主题切换（Light / Dark）。由 App 层实现。</summary>
public interface IThemeService
{
    bool IsDark { get; }
    Task SetThemeAsync(string theme, CancellationToken ct = default);
    event EventHandler? ThemeChanged;
}

/// <summary>动效开关。关闭后所有代码动画时长为 0。</summary>
public interface IMotionService
{
    bool Enabled { get; }
    void SetEnabled(bool enabled);
}
