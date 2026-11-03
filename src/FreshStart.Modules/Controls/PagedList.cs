using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FreshStart.Modules.Controls;

/// <summary>
/// 分页集合：默认显示 30 项，点「加载更多」再追加 30 项。
/// 筛选条件变化时回到第一页；「加载更多」只追加，避免滚动位置跳回顶部。
/// </summary>
public sealed class PagedList<T> : ObservableObject
{
    public const int PageSize = 30;

    private readonly Func<T, string, bool> _filter;
    private IReadOnlyList<T> _source = Array.Empty<T>();
    private List<T> _matches = new();
    private string _keyword = string.Empty;
    private int _visible;

    public PagedList(Func<T, string, bool>? filter = null)
    {
        _filter = filter ?? ((_, _) => true);
        LoadMoreCommand = new RelayCommand(LoadMore);
    }

    public ObservableCollection<T> Items { get; } = new();
    public IRelayCommand LoadMoreCommand { get; }

    public bool HasMore => Items.Count < _matches.Count;
    public int Total => _matches.Count;
    public int Loaded => Items.Count;
    public string MoreText => $"加载更多（已显示 {Items.Count} / {_matches.Count}）";
    public string Summary => $"共 {_matches.Count} 项，已显示 {Items.Count} 项";

    /// <summary>重新绑定数据源并回到第一页（用于首次加载、筛选条件变化）。</summary>
    public void Reset(IEnumerable<T> source, string? keyword = null)
    {
        _source = source as IReadOnlyList<T> ?? source.ToList();
        _keyword = keyword ?? string.Empty;
        Rebuild();
    }

    /// <summary>数据源内容变化后刷新（保留当前筛选词）。</summary>
    public void Refresh() => Rebuild();

    /// <summary>只更新筛选词并回到第一页。</summary>
    public void ApplyFilter(string? keyword)
    {
        _keyword = keyword ?? string.Empty;
        Rebuild();
    }

    private void Rebuild()
    {
        _matches = _source.Where(item => _filter(item, _keyword)).ToList();
        _visible = Math.Min(PageSize, _matches.Count);
        Items.Clear();
        AppendVisible();
        NotifyState();
    }

    private void LoadMore()
    {
        if (_visible >= _matches.Count) return;
        _visible = Math.Min(_visible + PageSize, _matches.Count);
        AppendVisible();
        NotifyState();
    }

    private void AppendVisible()
    {
        for (var index = Items.Count; index < _visible; index++) Items.Add(_matches[index]);
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(Loaded));
        OnPropertyChanged(nameof(MoreText));
        OnPropertyChanged(nameof(Summary));
    }
}
