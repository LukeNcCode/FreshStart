using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Modules.Visual;

namespace FreshStart.App.Services;

public sealed class ToastItem
{
    public ToastItem(string message, ToastKind kind)
    {
        Message = message;
        Kind = kind;
    }

    public string Message { get; }
    public ToastKind Kind { get; }
    public string IconData => AppIcons.Get(Kind switch
    {
        ToastKind.Success => "check",
        ToastKind.Warning => "alert",
        ToastKind.Error => "alert",
        _ => "info"
    });
}

/// <summary>右下角 Toast。3.5 秒自动消失，最多同时显示 2 条。</summary>
public sealed class ToastService : IToastService
{
    private Dispatcher? _dispatcher;
    private Controls.ToastHost? _host;

    public ObservableCollection<ToastItem> Items { get; } = new();

    public void Attach(Controls.ToastHost host, Dispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        host.ItemsSource = Items;
    }

    public void Show(string message, ToastKind kind = ToastKind.Info)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var dispatcher = _dispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess()) ShowCore(message, kind);
        else dispatcher.BeginInvoke(() => ShowCore(message, kind), DispatcherPriority.Normal);
    }

    private void ShowCore(string message, ToastKind kind)
    {
        var duplicate = Items.FirstOrDefault(item =>
            string.Equals(item.Message, message, StringComparison.Ordinal)
            && item.Kind == kind);
        if (duplicate is not null) Items.Remove(duplicate);

        while (Items.Count >= 2) Items.RemoveAt(0);
        var item = new ToastItem(message, kind);
        Items.Add(item);
        Log.Debug($"Toast: {message}");
        _ = DismissLaterAsync(item);
    }

    private async Task DismissLaterAsync(ToastItem item)
    {
        try
        {
            await Task.Delay(3500).ConfigureAwait(false);
            var dispatcher = _dispatcher;
            if (dispatcher is null) return;
            await dispatcher.InvokeAsync(() => Items.Remove(item));
        }
        catch
        {
            // 退出过程中忽略
        }
    }
}
