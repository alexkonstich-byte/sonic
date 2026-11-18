using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Sonic.Views;

namespace Sonic.Services;

public enum PageKey
{
    Home,
    Downloads,
    Library,
    Playlists,
    PlaylistDetail,
    History,
    Settings,
    YouTube,
}

public interface INavigationService
{
    event EventHandler<PageKey>? Navigated;
    PageKey? Current { get; }
    bool CanGoBack { get; }
    void Initialize(Frame frame);
    bool NavigateTo(PageKey page, object? parameter = null);
    void GoBack();
}

/// <summary>Page-key based navigation. Views receive their parameter through <see cref="INavigationAware"/>.</summary>
public sealed class NavigationService : INavigationService
{
    private static readonly Dictionary<PageKey, Type> Pages = new()
    {
        [PageKey.Home] = typeof(HomePage),
        [PageKey.Downloads] = typeof(DownloadsPage),
        [PageKey.Library] = typeof(LibraryPage),
        [PageKey.Playlists] = typeof(PlaylistsPage),
        [PageKey.PlaylistDetail] = typeof(PlaylistDetailPage),
        [PageKey.History] = typeof(HistoryPage),
        [PageKey.Settings] = typeof(SettingsPage),
        [PageKey.YouTube] = typeof(YouTubePage),
    };

    private Frame? _frame;

    public event EventHandler<PageKey>? Navigated;

    public PageKey? Current { get; private set; }

    public bool CanGoBack => _frame?.CanGoBack == true;

    public void Initialize(Frame frame)
    {
        _frame = frame;
        _frame.Navigated += OnNavigated;
    }

    public bool NavigateTo(PageKey page, object? parameter = null)
    {
        if (_frame is null) return false;
        var type = Pages[page];
        if (_frame.Content?.GetType() == type && parameter is null) return false;
        var transition = page == PageKey.PlaylistDetail
            ? (NavigationTransitionInfo)new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight }
            : new EntranceNavigationTransitionInfo();
        return _frame.Navigate(type, parameter, transition);
    }

    public void GoBack()
    {
        if (_frame?.CanGoBack == true) _frame.GoBack();
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        var key = Pages.FirstOrDefault(p => p.Value == e.SourcePageType).Key;
        Current = key;
        Navigated?.Invoke(this, key);
    }
}

/// <summary>Implemented by view models that need the navigation parameter or a hook when shown/hidden.</summary>
public interface INavigationAware
{
    Task OnNavigatedToAsync(object? parameter);
    void OnNavigatedFrom();
}
