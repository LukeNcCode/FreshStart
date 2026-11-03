using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Services.System;
using FreshStart.Services.Unattended;
using Microsoft.Win32;

namespace FreshStart.Modules.Unattended;

public sealed class UnattendedJobRow : ObservableObject
{
    public UnattendedJobRow(UnattendedJob job) => Job = job;

    public UnattendedJob Job { get; }
    public string Name => Job.Name;
    public string Description => Job.Description;
    public string KindText => "方案";
    public string StepsText => $"{Job.Steps.Count} 步";

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
    }
}

public sealed class UnattendedPlanItemRow
{
    public UnattendedPlanItemRow(UnattendedPlanItem item)
    {
        Title = item.Title;
        Detail = item.Detail;
        RiskText = item.Skipped ? "跳过" : item.RiskText;
        Irreversible = item.Irreversible;
        Skipped = item.Skipped;
        SkipReason = item.SkipReason;
        SourceStep = item.SourceStep;
    }

    public string Title { get; }
    public string Detail { get; }
    public string RiskText { get; }
    public bool Irreversible { get; }
    public bool Skipped { get; }
    public string SkipReason { get; }
    public UnattendedStep? SourceStep { get; }
}

public sealed partial class UnattendedViewModel : ObservableObject
{
    private readonly UnattendedService _service;
    private readonly OperationRunner _runner;
    private readonly IToastService _toast;
    private CancellationTokenSource? _cts;

    public UnattendedViewModel(UnattendedService service, OperationRunner runner, IToastService toast)
    {
        _service = service;
        _runner = runner;
        _toast = toast;
    }

    public ObservableCollection<UnattendedJobRow> Jobs { get; } = new();
    public ObservableCollection<UnattendedPlanItemRow> Items { get; } = new();

    [ObservableProperty] private UnattendedJobRow? _selectedJob;
    [ObservableProperty] private string _planSummary = "选择一个方案查看执行计划";
    [ObservableProperty] private string _warningText = string.Empty;
    [ObservableProperty] private bool _hasWarnings;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isBusy;
    [ObservableProperty] private bool _canRun;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "尚未执行";
    [ObservableProperty] private string _reportPath = string.Empty;
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private string _notesText = string.Empty;
    [ObservableProperty] private bool _hasNotes;
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editDescription = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelButtonText))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isCancelling;

    /// <summary>取消按钮文案：点过一次后变成「取消中…」。</summary>
    public string CancelButtonText => IsCancelling ? "取消中…" : "取消执行";

    public bool CanCancel => IsBusy && !IsCancelling;

    [RelayCommand]
    private void Load()
    {
        // 方案全部来自「方案工作台」导出或手动导入；这里不再内置预设（与一键开荒重复）
        if (Jobs.Count == 0)
        {
            PlanSummary = "还没有方案";
            StatusText = "点右上角「导入方案」，选择由「方案工作台」导出的 JSON 文件";
            CanRun = false;
        }
    }

    partial void OnSelectedJobChanged(UnattendedJobRow? value)
    {
        EditName = value?.Job.Name ?? string.Empty;
        EditDescription = value?.Job.Description ?? string.Empty;
        RefreshPlan();
    }

    private void RefreshPlan()
    {
        Items.Clear();
        HasWarnings = false;
        WarningText = string.Empty;
        HasNotes = false;
        NotesText = string.Empty;
        CanRun = false;

        if (SelectedJob is null)
        {
            PlanSummary = "选择一个方案查看执行计划";
            return;
        }

        try
        {
            var plan = _service.BuildPlan(SelectedJob.Job);
            foreach (var item in plan.Items) Items.Add(new UnattendedPlanItemRow(item));

            PlanSummary = plan.IsValid
                ? $"{plan.RunnableCount} 步 · {plan.Items.Count(item => item.Skipped)} 步跳过"
                : "方案没有可执行的项目";
            if (plan.HasIrreversible) PlanSummary += " · 含不可回滚步骤";

            if (plan.Warnings.Count > 0)
            {
                HasWarnings = true;
                WarningText = string.Join("\n", plan.Warnings);
            }

            CanRun = plan.IsValid;
            var restartHint = plan.Job.RestartExplorer && _service.RequiresExplorerRestart(plan.Job)
                ? "；完成后会自动重启资源管理器"
                : string.Empty;
            StatusText = plan.Job.Description + restartHint;
        }
        catch (Exception ex)
        {
            Log.Error("构建执行计划失败", ex);
            PlanSummary = "方案解析失败";
            HasWarnings = true;
            WarningText = ex.Message;
        }
    }

    [RelayCommand]
    private void ImportJob()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择执行方案（JSON）",
            Filter = "FreshStart 方案 (*.json)|*.json|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var job = _service.LoadFile(dialog.FileName);
            LoadJob(job);
            _toast.Show($"已载入方案「{job.Name}」", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Log.Error("载入执行方案失败", ex);
            _toast.Show($"方案无效：{ex.Message}", ToastKind.Error);
        }
    }

    public void LoadJob(UnattendedJob job)
    {
        job.IsBuiltIn = false;
        if (string.IsNullOrWhiteSpace(job.Key))
        {
            job.Key = "execution." + Guid.NewGuid().ToString("N")[..8];
        }

        try
        {
            var existing = Jobs.FirstOrDefault(row =>
                string.Equals(row.Job.Key, job.Key, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                var index = Jobs.IndexOf(existing);
                Jobs[index] = new UnattendedJobRow(job);
                SelectedJob = Jobs[index];
            }
            else
            {
                var row = new UnattendedJobRow(job);
                Jobs.Add(row);
                SelectedJob = row;
            }
        }
        catch (Exception ex)
        {
            Log.Error("载入执行方案失败", ex);
            _toast.Show($"载入执行方案失败：{ex.Message}", ToastKind.Error);
        }
    }

    [RelayCommand]
    private async Task RunJobAsync()
    {
        if (IsBusy || SelectedJob is null) return;

        var plan = _service.BuildPlan(SelectedJob.Job);
        if (!plan.IsValid)
        {
            _toast.Show("方案没有可执行的项目", ToastKind.Warning);
            return;
        }

        IsBusy = true;
        CanRun = false;
        IsCancelling = false;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        Progress = 0;
        HasReport = false;
        ReportPath = string.Empty;
        HasNotes = false;
        NotesText = string.Empty;
        StatusText = "正在准备…";
        WindowsNotifier.Show("FreshStart 执行开始", $"{plan.Job.Name} · {plan.RunnableCount} 步");

        try
        {
            var progress = new Progress<OperationStep>(step =>
            {
                if (step.Percent is { } percent) Progress = percent;
                if (!string.IsNullOrWhiteSpace(step.Message)) StatusText = step.Message;
            });

            var result = await _service.RunAsync(plan, _runner, progress, _cts.Token);

            if (result.Notes.Count > 0 || result.Failures.Count > 0)
            {
                HasNotes = true;
                NotesText = string.Join("\n",
                    result.Notes.Concat(result.Failures.Select(failure => "失败：" + failure)));
            }

            if (result.NothingToDo)
            {
                StatusText = "没有可执行的步骤（可能全部被跳过）";
                _toast.Show(StatusText, ToastKind.Warning);
                WindowsNotifier.Show("FreshStart 未执行", StatusText);
                return;
            }

            var execution = result.Execution!;
            if (result.ReportPath is not null)
            {
                ReportPath = result.ReportPath;
                HasReport = true;
            }

            if (execution.Cancelled)
            {
                var byUser = _cts?.IsCancellationRequested == true;
                StatusText = byUser
                    ? "已取消：已完成步骤保留，未执行的跳过（详见报告）"
                    : "已取消（未写入任何内容）";
                _toast.Show(byUser
                    ? "已取消：已完成的步骤保留，剩余步骤未执行"
                    : "已取消：确认弹窗中没有点击「启用并继续」或「不启用，继续」；需要时请重新点击「开始执行」",
                    ToastKind.Warning);
                WindowsNotifier.Show("FreshStart 执行已取消", $"{plan.Job.Name} · {StatusText}");
                return;
            }

            Progress = 100;
            StatusText = execution.Success
                ? result.Failures.Count > 0
                    ? $"执行完成：{result.Failures.Count} 步失败并跳过，成功步骤保持应用（详见报告）"
                    : $"执行完成：{execution.Result.Message}"
                : execution.RolledBack
                    ? $"执行失败，已回滚：{execution.Result.Message}"
                    : $"执行失败：{execution.Result.Message}";

            _toast.Show(StatusText,
                !execution.Success ? ToastKind.Error
                : result.Failures.Count > 0 ? ToastKind.Warning
                : ToastKind.Success);

            WindowsNotifier.Show(
                execution.Success
                    ? result.Failures.Count > 0 ? "FreshStart 执行完成（有失败步骤）" : "FreshStart 执行完成"
                    : "FreshStart 执行失败",
                StatusText,
                !execution.Success || result.Failures.Count > 0);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消：已完成步骤保留，未执行的跳过";
            _toast.Show(StatusText, ToastKind.Warning);
            WindowsNotifier.Show("FreshStart 执行已取消", $"{plan.Job.Name} · 已取消");
        }
        catch (Exception ex)
        {
            Log.Error("执行中心运行失败", ex);
            StatusText = $"执行失败：{ex.Message}";
            _toast.Show(StatusText, ToastKind.Error);
            WindowsNotifier.Show("FreshStart 执行失败", StatusText, isError: true);
        }
        finally
        {
            IsBusy = false;
            IsCancelling = false;
            CanRun = SelectedJob is not null && Items.Count > 0;
        }
    }

    /// <summary>
    /// 取消执行：当前步骤（如 winget 安装）被杀掉进程树后停止，已完成的步骤保留，
    /// 未执行的跳过；回滚阶段由 OperationRunner 提示且不可中断。
    /// </summary>
    [RelayCommand]
    private void CancelRun()
    {
        if (_cts is null || _cts.IsCancellationRequested || !IsBusy) return;
        IsCancelling = true;
        StatusText = "正在取消…（当前步骤收尾后停止，随后回滚本次变更）";
        try { _cts.Cancel(); } catch { /* ignore */ }
    }

    [RelayCommand]
    private void RemoveStep(UnattendedPlanItemRow? row)
    {
        if (row?.SourceStep is null) return;
        if (!CanEditSteps())
        {
            _toast.Show("该方案为只读预设；请先「另存为」成自定义方案", ToastKind.Warning);
            return;
        }

        SelectedJob!.Job.Steps.Remove(row.SourceStep);
        RefreshPlan();
        StatusText = "已删除一个步骤；点「另存为」保存修改";
    }

    [RelayCommand]
    private void MoveStepUp(UnattendedPlanItemRow? row) => MoveStep(row, -1);

    [RelayCommand]
    private void MoveStepDown(UnattendedPlanItemRow? row) => MoveStep(row, 1);

    private void MoveStep(UnattendedPlanItemRow? row, int delta)
    {
        if (row?.SourceStep is null || !CanEditSteps()) return;

        var steps = SelectedJob!.Job.Steps;
        var index = steps.IndexOf(row.SourceStep);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= steps.Count) return;

        (steps[index], steps[target]) = (steps[target], steps[index]);
        RefreshPlan();
        StatusText = "已调整步骤顺序；点「另存为」保存修改";
    }

    private bool CanEditSteps() => SelectedJob is not null && !SelectedJob.Job.IsBuiltIn;

    [RelayCommand]
    private void SaveJobEdits()
    {
        if (SelectedJob is null) return;

        var name = EditName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            _toast.Show("方案名称不能为空", ToastKind.Warning);
            return;
        }

        SelectedJob.Job.Name = name;
        SelectedJob.Job.Description = EditDescription.Trim();
        SelectedJob.Refresh();
        StatusText = "方案信息已更新；点「另存为」可保存到文件";
        _toast.Show("方案信息已更新", ToastKind.Success);
    }

    [RelayCommand]
    private void RemoveJob()
    {
        if (SelectedJob is null) return;
        if (SelectedJob.Job.IsBuiltIn)
        {
            _toast.Show("只读预设不能删除", ToastKind.Warning);
            return;
        }

        var name = SelectedJob.Name;
        Jobs.Remove(SelectedJob);
        SelectedJob = Jobs.FirstOrDefault();
        _toast.Show($"已移除方案「{name}」", ToastKind.Success);
    }

    [RelayCommand]
    private void SaveJobAs()
    {
        if (SelectedJob is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "另存为方案",
            Filter = "FreshStart 方案 (*.json)|*.json",
            FileName = string.IsNullOrWhiteSpace(EditName) ? SelectedJob.Job.Name + ".json" : EditName + ".json"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            SelectedJob.Job.Name = string.IsNullOrWhiteSpace(EditName) ? SelectedJob.Job.Name : EditName.Trim();
            SelectedJob.Job.Description = EditDescription.Trim();
            _service.Export(SelectedJob.Job, dialog.FileName);
            StatusText = $"已另存为：{dialog.FileName}";
            _toast.Show("方案已保存", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Log.Error("另存方案失败", ex);
            _toast.Show($"保存失败：{ex.Message}", ToastKind.Error);
        }
    }

    [RelayCommand]
    private void OpenReport()
    {
        if (string.IsNullOrWhiteSpace(ReportPath) || !File.Exists(ReportPath))
        {
            _toast.Show("报告尚未生成", ToastKind.Warning);
            return;
        }

        Open(ReportPath);
    }

    [RelayCommand]
    private void OpenReportsFolder()
    {
        var directory = AppPaths.ReportsRoot;
        Directory.CreateDirectory(directory);
        Open(directory);
    }

    private void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toast.Show($"打开失败：{ex.Message}", ToastKind.Warning);
        }
    }
}
