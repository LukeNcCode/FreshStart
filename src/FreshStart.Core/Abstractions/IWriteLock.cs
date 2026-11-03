namespace FreshStart.Core.Abstractions;

/// <summary>
/// 全局写锁：所有会修改系统状态的操作都要经过它，
/// 保证"注册表/服务/文件"在同一时刻只有一条写入链路。
/// 与 <see cref="IOperationGate"/>（用户确认弹窗）不是一回事。
/// 实现不可重入：复合操作内部请调用服务的"已持锁"版本，不要再次 EnterAsync。
/// </summary>
public interface IWriteLock
{
    /// <summary>获取写锁租约；用 <c>using</c> 释放。</summary>
    Task<IDisposable> EnterAsync(CancellationToken ct = default);
}
