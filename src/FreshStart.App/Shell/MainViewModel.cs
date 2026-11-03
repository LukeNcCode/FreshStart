using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.App.Services;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Settings;
using FreshStart.Modules.Navigation;
using FreshStart.Services.System;

namespace FreshStart.App.Shell;

public sealed partial class SidebarItem : ObservableObject
{
    public SidebarItem(NavItemDefinition definition) => Definition = definition;

    public NavItemDefinition Definition { get; }
    public string Key => Definition.Key;
    public string Title => Definition.Title;
    public string IconData => Definition.IconData;
    public bool IsAvailable => Definition.IsAvailable;
    public string Hint => IsAvailable ? Definition.Hint : Definition.UnavailableText;
    public string BatchText => $"第 {Definition.Batch} 批";

    [ObservableProperty]
    private bool _isSelected;
}

public sealed class SidebarGroup
{
    public SidebarGroup(string title, IEnumerable<SidebarItem> items)
    {
        Title = title;
        Items = new ObservableCollection<SidebarItem>(items);
    }

    public string Title { get; }
    public ObservableCollection<SidebarItem> Items { get; }
}

/// <summary>主窗口 ViewModel：导航分组、主题按钮、侧边栏状态。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IReadOnlyList<SidebarGroup> _allGroups;
    private readonly IReadOnlyList<SidebarItem> _allItems;

    public MainViewModel(SettingsService settings, IThemeService theme)
    {
        _settings = settings;
        _theme = theme;

        _allGroups = NavCatalog.Grouped()
            .Select(group => new SidebarGroup(group.Key, group.Select(item => new SidebarItem(item))))
            .ToList();
        _allItems = _allGroups.SelectMany(group => group.Items).ToList();
        ApplyNavFilter(string.Empty);

        _isDark = theme.IsDark;
        UpdateThemeVisuals();
        _isSidebarCollapsed = settings.Current.SidebarCollapsed;
        IsElevated = ElevationService.IsAdministrator;
        ElevationLabel = IsElevated ? "管理员模式" : "标准权限";
        ElevationTooltip = IsElevated
            ? "当前以管理员身份运行，可执行需要系统权限的操作"
            : "当前以标准权限运行；涉及系统设置的操作会请求提权或提示失败";

        theme.ThemeChanged += (_, _) =>
        {
            IsDark = theme.IsDark;
            UpdateThemeVisuals();
        };
    }

    public ObservableCollection<SidebarGroup> Groups { get; } = new();

    public IEnumerable<SidebarItem> AllItems => _allItems;

    public bool IsElevated { get; }

    public string ElevationLabel { get; }

    public string ElevationTooltip { get; }

    [ObservableProperty]
    private string _navSearch = string.Empty;

    [ObservableProperty]
    private bool _hasNoNavResults;

    partial void OnNavSearchChanged(string value) => ApplyNavFilter(value);

    private void ApplyNavFilter(string keyword)
    {
        var query = keyword.Trim();
        Groups.Clear();

        foreach (var group in _allGroups)
        {
            var items = string.IsNullOrWhiteSpace(query)
                ? group.Items.ToList()
                : group.Items.Where(item =>
                        item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || item.Hint.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || group.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    .ToList();
            if (items.Count > 0) Groups.Add(new SidebarGroup(group.Title, items));
        }

        HasNoNavResults = Groups.Count == 0;
    }

    [ObservableProperty]
    private bool _isDark;

    [ObservableProperty]
    private string _themeIconData = string.Empty;

    [ObservableProperty]
    private string _themeTooltip = string.Empty;

    [ObservableProperty]
    private bool _isSidebarCollapsed;

    [ObservableProperty]
    private string _titleText = "FreshStart";

    [RelayCommand]
    private async Task ToggleThemeAsync()
    {
        var target = IsDark ? "Light" : "Dark";
        await _theme.SetThemeAsync(target);
        await _settings.UpdateAsync(settings => settings.Theme = target);
    }

    public void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        _ = _settings.UpdateAsync(settings => settings.SidebarCollapsed = IsSidebarCollapsed);
    }

    public void SetSelected(string key)
    {
        foreach (var item in AllItems)
        {
            item.IsSelected = string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetTitle(string title) => TitleText = title;

    private void UpdateThemeVisuals()
    {
        ThemeIconData = ShellIcons.Theme(IsDark);
        ThemeTooltip = IsDark ? "切换到浅色模式" : "切换到深色模式";
    }
}
