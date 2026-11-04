using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Services.Migration;
using Microsoft.Win32;

namespace FreshStart.Modules.Migration;

public sealed partial class MigrationItemViewModel : ObservableObject
{
    public MigrationItemViewModel(MigrationCandidate candidate)
    {
        Candidate = candidate;
    }

    public MigrationCandidate Candidate { get; }
    public string Name => Candidate.Name;
    public string Description => Candidate.Description;
    public string Version => string.IsNullOrWhiteSpace(Candidate.Version) ? "版本未知" : Candidate.Version;
    public string KindText => Candidate.Kind == MigrationItemKind.Software ? "软件" : "开发环境";
    public bool IsSoftware => Candidate.Kind == MigrationItemKind.Software;
    public bool IsInstalled => Candidate.IsInstalled;
    public bool HasPayload => Candidate.HasPayload;
    public string InstallState => Candidate.IsInstalled ? "已安装" : Candidate.HasPayload ? "发现数据" : "未检测";
    public string SelectionState => Candidate.Kind == MigrationItemKind.Software
        ? Candidate.DataRoots.Count == 0
            ? "尚未添加数据目录"
            : string.Join(Environment.NewLine, Candidate.DataRoots)
        : Candidate.ToolRoot;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _installerDirectory = string.Empty;

    public bool HasManualInstaller => InstallerDirectory.Length > 0;

    partial void OnInstallerDirectoryChanged(string value) => OnPropertyChanged(nameof(HasManualInstaller));

    public void NotifyDataRootsChanged()
    {
        OnPropertyChanged(nameof(SelectionState));
        OnPropertyChanged(nameof(Description));
    }
}

public sealed partial class EnvironmentMigrationViewModel : ObservableObject
{
    private readonly EnvironmentMigrationService _service;
    private readonly IToastService _toast;
    private readonly IUserPromptService _prompts;
    private CancellationTokenSource? _cts;

    public EnvironmentMigrationViewModel(
        EnvironmentMigrationService service,
        IToastService toast,
        IUserPromptService prompts)
    {
        _service = service;
        _toast = toast;
        _prompts = prompts;
    }

    public ObservableCollection<MigrationItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "扫描本机软件与开发环境…";

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _selectedText = "0";

    public async Task LoadAsync()
    {
        if (Items.Count > 0 || IsLoading) return;
        await ScanAsync();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;
        IsLoading = true;
        StatusText = "正在扫描已安装软件、开发工具和数据目录…";
        try
        {
            var selected = Items.Where(item => item.IsSelected).Select(item => item.Candidate.Id).ToHashSet();
            var candidates = await _service.ScanAsync();
            Items.Clear();
            foreach (var candidate in candidates)
            {
                var item = new MigrationItemViewModel(candidate)
                {
                    IsSelected = selected.Contains(candidate.Id)
                };
                item.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(MigrationItemViewModel.IsSelected))
                    {
                        UpdateSelectedText();
                    }
                };
                Items.Add(item);
            }
            UpdateSelectedText();
            StatusText = $"扫描完成：{Items.Count(item => item.IsInstalled)} 项已安装，" +
                         $"{Items.Count(item => item.HasPayload)} 项包含可迁移数据";
        }
        catch (Exception ex)
        {
            Log.Error("扫描环境克隆内容失败", ex);
            StatusText = $"扫描失败：{ex.Message}";
            _toast.Show("扫描环境克隆内容失败", ToastKind.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ChooseInstaller(MigrationItemViewModel? item)
    {
        if (item is null) return;
        var dialog = new OpenFolderDialog
        {
            Title = $"选择 {item.Name} 的本地安装器目录",
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            item.InstallerDirectory = dialog.FolderName;
            item.IsSelected = true;
            UpdateSelectedText();
        }
    }

    [RelayCommand]
    private void ChooseDataDirectory(MigrationItemViewModel? item)
    {
        if (item is null) return;
        var dialog = new OpenFolderDialog
        {
            Title = $"选择 {item.Name} 的聊天或下载数据目录",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        var roots = item.Candidate.DataRoots.ToList();
        if (!roots.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
        {
            roots.Add(dialog.FolderName);
            item.Candidate.DataRoots = roots;
            item.NotifyDataRootsChanged();
            item.IsSelected = true;
            UpdateSelectedText();
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (IsBusy) return;
        var selected = Items.Where(item => item.IsSelected).ToList();
        if (selected.Count == 0)
        {
            _toast.Show("请至少选择一项", ToastKind.Warning);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "生成环境克隆包",
            Filter = "FreshStart 克隆包 (*.fclone)|*.fclone",
            FileName = $"FreshStart-Clone-{DateTime.Now:yyyyMMdd-HHmmss}.fclone",
            AddExtension = true,
            DefaultExt = ".fclone"
        };
        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        ProgressPercent = 0;
        ProgressText = "准备提取…";
        _cts = new CancellationTokenSource();
        try
        {
            var installerDirectories = selected
                .Where(item => item.InstallerDirectory.Length > 0)
                .ToDictionary(item => item.Candidate.Id, item => item.InstallerDirectory);
            var progress = new Progress<MigrationProgress>(value =>
            {
                ProgressText = value.Message;
                ProgressPercent = value.Percent;
            });
            var package = await _service.ExportAsync(
                selected.Select(item => item.Candidate).ToList(),
                installerDirectories,
                dialog.FileName,
                progress,
                _cts.Token);
            StatusText = $"克隆包已生成：{package}";
            _toast.Show("环境克隆包已生成", ToastKind.Success);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消提取";
            _toast.Show("已取消生成克隆包", ToastKind.Warning);
        }
        catch (Exception ex)
        {
            Log.Error("生成环境克隆包失败", ex);
            StatusText = $"生成失败：{ex.Message}";
            _toast.Show(StatusText, ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            ProgressText = string.Empty;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (IsBusy) return;
        var dialog = new OpenFileDialog
        {
            Title = "恢复环境克隆包",
            Filter = "FreshStart 克隆包 (*.fclone)|*.fclone|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        MigrationPackagePreview preview;
        try
        {
            preview = await _service.InspectAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            StatusText = $"无法读取克隆包：{ex.Message}";
            _toast.Show(StatusText, ToastKind.Error);
            return;
        }

        var relocationRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var details = new List<string>();
        details.AddRange(preview.Items.Take(6));
        if (preview.Targets.Count > 0)
        {
            details.Add("恢复目标：");
            var targetLines = new List<string>();
            foreach (var target in preview.Targets)
            {
                if (!target.RequiresRelocation)
                {
                    targetLines.Add(target.OriginalPath);
                    continue;
                }

                var folderDialog = new OpenFolderDialog
                {
                    Title = $"{target.ItemName} 的数据盘不可用，请选择替代部署位置",
                    Multiselect = false,
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                };
                if (folderDialog.ShowDialog() != true)
                {
                    StatusText = "已取消选择迁移数据位置";
                    return;
                }
                relocationRoots[target.OriginalPath] = folderDialog.FolderName;
                var resolved = EnvironmentMigrationService.BuildRelocationTarget(
                    target.ItemId, target.OriginalPath, folderDialog.FolderName);
                targetLines.Add($"{target.OriginalPath} → {resolved}");
            }
            details.AddRange(targetLines.Take(12));
        }
        details.Add("聊天软件需要关闭后再恢复数据");
        details.Add("已有的同名数据目录会先移动到同级的 freshstart-backup 目录");
        details.Add("开发环境会恢复到 FreshStart 独立目录并更新用户 PATH");
        details.Add("迁移包不包含登录令牌、浏览器密码或聊天软件登录状态");
        details.Add("软件安装不保证可以自动回滚");

        var confirmed = await _prompts.ConfirmAsync(new UserPrompt
        {
            Title = "开始离线恢复？",
            Message = "将从克隆包安装软件、恢复聊天数据并部署开发环境。",
            Details = details,
            ConfirmText = "开始恢复",
            CancelText = "取消"
        });
        if (!confirmed) return;

        IsBusy = true;
        ProgressPercent = 0;
        ProgressText = "正在读取克隆包…";
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<MigrationProgress>(value =>
            {
                ProgressText = value.Message;
                ProgressPercent = value.Percent;
            });
            var result = await _service.RestoreAsync(
                dialog.FileName, relocationRoots, progress, _cts.Token);
            StatusText = $"恢复完成：软件 {result.InstalledApplications} 个，" +
                         $"数据目录 {result.RestoredDataSets} 个，" +
                         $"开发环境 {result.RestoredEnvironments} 个";
            var message = result.Warnings.Count == 0
                ? "环境恢复完成，请重新登录相关软件"
                : $"环境恢复完成，有 {result.Warnings.Count} 条提示";
            _toast.Show(message, result.Warnings.Count == 0 ? ToastKind.Success : ToastKind.Warning);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消恢复";
            _toast.Show("已取消恢复克隆包", ToastKind.Warning);
        }
        catch (Exception ex)
        {
            Log.Error("恢复环境克隆包失败", ex);
            StatusText = $"恢复失败：{ex.Message}";
            _toast.Show(StatusText, ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            ProgressText = string.Empty;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    public void UpdateSelectedText()
    {
        SelectedText = Items.Count(item => item.IsSelected).ToString();
    }
}
