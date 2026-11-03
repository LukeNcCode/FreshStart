using FreshStart.Core.Abstractions;
using FreshStart.Modules.Navigation;

namespace FreshStart.App.Services;

/// <summary>页面导航。ViewModel 缓存复用，视图由 DataTemplate 按类型创建。</summary>
public sealed class NavigationService : INavigationService
{
    private readonly IReadOnlyDictionary<string, Func<object>> _factories;
    private readonly Dictionary<string, object> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IToastService _toast;

    public NavigationService(IReadOnlyDictionary<string, Func<object>> factories, IToastService toast)
    {
        _factories = factories;
        _toast = toast;
    }

    public string CurrentKey { get; private set; } = string.Empty;

    public event EventHandler<NavigationChangedEventArgs>? Navigated;

    public bool Navigate(string key)
    {
        key = key.ToLowerInvariant() switch
        {
            "unattended" or "script-editor" => "execution",
            _ => key
        };

        var meta = NavCatalog.Find(key);
        if (meta is null)
        {
            _toast.Show("未找到该页面", ToastKind.Warning);
            return false;
        }

        if (!meta.IsAvailable)
        {
            _toast.Show(meta.UnavailableText);
            return false;
        }

        if (!_factories.TryGetValue(meta.Key, out var factory))
        {
            _toast.Show($"「{meta.Title}」尚未注册", ToastKind.Warning);
            return false;
        }

        if (!_cache.TryGetValue(meta.Key, out var viewModel))
        {
            viewModel = factory();
            _cache[meta.Key] = viewModel;
        }

        CurrentKey = meta.Key;
        Navigated?.Invoke(this, new NavigationChangedEventArgs
        {
            Key = meta.Key,
            Title = meta.Title,
            ViewModel = viewModel
        });
        return true;
    }
}
