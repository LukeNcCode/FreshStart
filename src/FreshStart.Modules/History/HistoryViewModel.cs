using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Services.History;

namespace FreshStart.Modules.History;

public sealed class HistoryRow
{
    public HistoryRow(OperationRecord record) => Record = record;

    public OperationRecord Record { get; }
    public string TimeText => Record.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Title => Record.OperationName;
    public string ResultText => Record.Cancelled ? "已取消" : Record.Success ? "成功" : "失败";
    public string RiskText => Record.Risk switch
    {
        RiskLevel.High => "高风险",
        RiskLevel.Caution => "谨慎",
        _ => "安全"
    };
    public string DetailText => $"{RiskText} · {Record.ChangeCount} 项变更 · {ResultText}";
    public bool CanRestore => Record.Success && !Record.RolledBack
                              && !string.IsNullOrWhiteSpace(Record.BackupDirectory)
                              && Directory.Exists(Record.BackupDirectory);
}

public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly HistoryRestoreService _service;
    private readonly IUserPromptService _prompts;
    private readonly IToastService _toast;

    public HistoryViewModel(HistoryRestoreService service, IUserPromptService prompts, IToastService toast)
    {
        _service = service;
        _prompts = prompts;
        _toast = toast;
    }

    public ObservableCollection<HistoryRow> Records { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "正在读取操作历史…";

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (Records.Count > 0) return;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync();

    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var records = await _service.ListAsync(100);
            Records.Clear();
            foreach (var record in records) Records.Add(new HistoryRow(record));
            StatusText = Records.Count == 0
                ? "暂无操作记录"
                : "可回滚注册表值、注册表键与服务启动类型；文件、计划任务、Appx 等条目会跳过";
        }
        catch (Exception ex)
        {
            StatusText = "读取历史失败，请查看日志";
            _toast.Show("读取操作历史失败", ToastKind.Error);
            FreshStart.Core.Log.Error("读取操作历史失败", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync(HistoryRow? row)
    {
        if (row is null || IsBusy || !row.CanRestore) return;
        var confirmed = await _prompts.ConfirmAsync(new UserPrompt
        {
            Title = $"回滚「{row.Title}」？",
            Message = $"将恢复到该操作执行前的状态（{row.TimeText}）。",
            Details = new[]
            {
                "会覆盖之后的同类设置，请确认这是你想恢复的时间点",
                "仅回放注册表值/键与服务启动类型；文件、任务、Appx 会跳过",
                "回滚前不会自动创建新备份"
            },
            ConfirmText = "回滚到此处",
            CancelText = "取消",
            IsDangerous = true
        });
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var result = await _service.RestoreAsync(row.Record, CancellationToken.None);
            _toast.Show(result.Message, result.Success ? ToastKind.Success : ToastKind.Warning);
            StatusText = result.Message;
        }
        catch (Exception ex)
        {
            FreshStart.Core.Log.Error("历史回滚失败", ex);
            _toast.Show($"回滚失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenBackup(HistoryRow? row)
    {
        var path = row?.Record.BackupDirectory;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }
}
