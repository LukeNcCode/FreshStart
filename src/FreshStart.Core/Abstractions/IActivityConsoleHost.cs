namespace FreshStart.Core.Abstractions;

/// <summary>由 App 层实现的独立任务控制台窗口宿主与活动发布入口。</summary>
public interface IActivityConsoleHost
{
    bool IsEnabled { get; }
    event EventHandler<bool>? EnabledChanged;
    void SetEnabled(bool enabled);
}
