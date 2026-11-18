using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;
using Sonic.Helpers;
using Sonic.Services;

namespace Sonic.ViewModels;

public sealed partial class FeedItemViewModel(MediaInfo info) : ObservableObject
{
    public MediaInfo Info { get; } = info;
    public string Title => Info.Title;
    public string Channel => Info.Uploader ?? "Unknown channel";
    public string DurationText => Info.DurationSeconds is > 0 ? Format.Duration(Info.DurationSeconds) : string.Empty;
    public string? Thumbnail => Info.ThumbnailUrl ?? (Info.Id.Length == 11 ? $"https://i.ytimg.com/vi/{Info.Id}/mqdefault.jpg" : null);
    public string Url => Info.Url;

    [ObservableProperty] private bool _isSelected;

    /// <summary>Found in the download history (shown as a badge).</summary>
    [ObservableProperty] private bool _isDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQueued))]
    private string? _queuedAs;

    public bool IsQueued => QueuedAs is not null;
}

public sealed record FeedDefinition(string Key, string Name, string Feed, string Empty);

/// <summary>
/// The signed-in user's YouTube lists (history, liked, Watch later, subscriptions) read through yt-dlp with the
/// imported cookies, with one-click audio/video downloads.
/// </summary>
public sealed partial class YouTubeViewModel : ObservableObject, INavigationAware
{
    private const int PageSize = 50;

    private readonly IYtDlpService _ytDlp;
    private readonly ICookieService _cookies;
    private readonly IDownloadQueueService _queue;
    private readonly INavigationService _navigation;
    private readonly IShellLauncher _launcher;
    private readonly IInAppNotificationService _notifications;
    private readonly IZapretAdvisor _zapret;
    private readonly ILogger<YouTubeViewModel> _logger;
    private readonly Dictionary<string, List<FeedItemViewModel>> _cache = [];
    private readonly Dictionary<string, bool> _hasMore = [];
    private CancellationTokenSource? _cts;

    public YouTubeViewModel(
        IYtDlpService ytDlp,
        ICookieService cookies,
        IDownloadQueueService queue,
        INavigationService navigation,
        IShellLauncher launcher,
        IInAppNotificationService notifications,
        IZapretAdvisor zapret,
        IUiDispatcher dispatcher,
        ILogger<YouTubeViewModel> logger)
    {
        _ytDlp = ytDlp;
        _cookies = cookies;
        _queue = queue;
        _navigation = navigation;
        _launcher = launcher;
        _notifications = notifications;
        _zapret = zapret;
        _logger = logger;
        _selectedFeed = Feeds[0];
        // Raised on a background thread (imports run off the UI thread).
        cookies.Changed += (_, _) => dispatcher.Post(() =>
        {
            var wasSignedIn = IsSignedIn;
            RefreshSignIn();
            _cache.Clear(); // lists belong to the previous account
            if (IsSignedIn && !wasSignedIn) _ = LoadAsync(reset: true);
            else ApplyFilter();
        });
    }

    public IReadOnlyList<FeedDefinition> Feeds { get; } =
    [
        new("history", "History", ":ythistory", "Your watch history is empty or paused."),
        new("liked", "Liked", ":ytfav", "You haven't liked any videos yet."),
        new("later", "Watch later", ":ytwatchlater", "Your Watch later list is empty."),
        new("subs", "Subscriptions", ":ytsubs", "No recent videos from your subscriptions."),
    ];

    [ObservableProperty] private ObservableCollection<FeedItemViewModel> _items = [];
    [ObservableProperty] private FeedDefinition _selectedFeed;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoadingMore;
    [ObservableProperty] private bool _canLoadMore;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string _signInText = string.Empty;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string? _emptyText;
    [ObservableProperty] private int _selectedCount;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public async Task OnNavigatedToAsync(object? parameter)
    {
        RefreshSignIn();
        if (IsSignedIn && !_cache.ContainsKey(SelectedFeed.Key)) await LoadAsync(reset: true);
    }

    public void OnNavigatedFrom() { }

    private void RefreshSignIn()
    {
        var status = _cookies.GetStatus();
        IsSignedIn = status.Mode == CookieMode.Browser || (status.Mode == CookieMode.Imported && status.Imported is { YouTubeCount: > 0 });
        SignInText = status.Mode switch
        {
            CookieMode.Browser => $"Using your session from {status.BrowserName}",
            CookieMode.Imported when status.Imported is { SignedInToGoogle: true } => $"Signed in with cookies from {status.Source}",
            CookieMode.Imported => $"Cookies from {status.Source} — no YouTube sign-in was found in them",
            _ => "Not signed in",
        };
    }

    public async Task SelectFeedAsync(string key)
    {
        var feed = Feeds.FirstOrDefault(f => f.Key == key);
        if (feed is null || feed == SelectedFeed) return;
        SelectedFeed = feed;
        if (_cache.ContainsKey(feed.Key)) ApplyFilter();
        else await LoadAsync(reset: true);
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync(reset: true);

    [RelayCommand]
    private Task LoadMore() => LoadAsync(reset: false);

    private async Task LoadAsync(bool reset)
    {
        if (!IsSignedIn)
        {
            Error = null;
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var feed = SelectedFeed;
        var list = reset || !_cache.TryGetValue(feed.Key, out var existing) ? [] : existing;
        if (reset) IsLoading = true;
        else IsLoadingMore = true;
        Error = null;

        try
        {
            var page = await Task.Run(() => _ytDlp.GetFeedAsync(feed.Feed, list.Count + 1, PageSize, token), token);
            var known = list.Select(i => i.Info.Id).ToHashSet();
            var added = page.Items.Where(i => !string.IsNullOrEmpty(i.Url) && known.Add(i.Id)).Select(CreateItem).ToList();
            list.AddRange(added);
            _cache[feed.Key] = list;
            _hasMore[feed.Key] = page.HasMore;
            if (feed == SelectedFeed) ApplyFilter();
            await MarkDownloadedAsync(added, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is YtDlpException or DependencyMissingException)
        {
            _logger.LogWarning(ex, "Loading YouTube feed {Feed} failed", feed.Feed);
            Error = ex.Message;
            _zapret.MaybeSuggest(ex.Message, "https://www.youtube.com/");
        }
        finally
        {
            IsLoading = false;
            IsLoadingMore = false;
        }
    }

    private async Task MarkDownloadedAsync(IReadOnlyList<FeedItemViewModel> items, CancellationToken token)
    {
        foreach (var item in items)
        {
            if (token.IsCancellationRequested) return;
            try
            {
                if (await _queue.FindPreviousDownloadAsync(item.Url, token) is not null) item.IsDownloaded = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "History lookup failed");
                return;
            }
        }
    }

    [RelayCommand]
    private Task Preview(FeedItemViewModel item) => App.GetService<PlayerViewModel>().PreviewAsync(item.Info);

    private FeedItemViewModel CreateItem(MediaInfo info)
    {
        var item = new FeedItemViewModel(info);
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FeedItemViewModel.IsSelected)) SelectedCount = Items.Count(i => i.IsSelected);
        };
        return item;
    }

    private void ApplyFilter()
    {
        var all = _cache.TryGetValue(SelectedFeed.Key, out var list) ? list : [];
        var q = SearchText.Trim();
        IEnumerable<FeedItemViewModel> items = all;
        if (q.Length > 0)
            items = items.Where(i => i.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || i.Channel.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        Items = new ObservableCollection<FeedItemViewModel>(items);
        CanLoadMore = _hasMore.GetValueOrDefault(SelectedFeed.Key) && q.Length == 0;
        EmptyText = Items.Count == 0 && !IsLoading && Error is null && _cache.ContainsKey(SelectedFeed.Key)
            ? (q.Length > 0 ? "Nothing matches your search." : SelectedFeed.Empty)
            : null;
        SelectedCount = Items.Count(i => i.IsSelected);
    }

    [RelayCommand]
    private Task DownloadAudio(FeedItemViewModel item) => EnqueueAsync([item], MediaKind.Audio);

    [RelayCommand]
    private Task DownloadVideo(FeedItemViewModel item) => EnqueueAsync([item], MediaKind.Video);

    [RelayCommand]
    private Task DownloadSelected(string kind) =>
        EnqueueAsync(Items.Where(i => i.IsSelected).ToList(), kind == "video" ? MediaKind.Video : MediaKind.Audio);

    private async Task EnqueueAsync(IReadOnlyList<FeedItemViewModel> items, MediaKind kind)
    {
        var added = 0;
        foreach (var item in items)
        {
            try
            {
                await _queue.EnqueueAsync(new DownloadRequest
                {
                    Url = item.Url,
                    Kind = kind,
                    Title = item.Title,
                    Artist = item.Info.Uploader,
                    ThumbnailUrl = item.Thumbnail,
                    DurationSeconds = item.Info.DurationSeconds,
                });
                item.QueuedAs = kind == MediaKind.Video ? "Video queued" : "Audio queued";
                item.IsSelected = false;
                added++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                _notifications.ShowError("Couldn't add the download", ex.Message);
            }
        }

        if (added > 1) _notifications.ShowSuccess("Added to downloads", Format.Count(added, kind == MediaKind.Video ? "video" : "song"));
    }

    [RelayCommand]
    private void MoreOptions(FeedItemViewModel item) => _navigation.NavigateTo(PageKey.Home, item.Url);

    [RelayCommand]
    private void OpenOnYouTube(FeedItemViewModel item) => _launcher.OpenUri(item.Url);

    [RelayCommand]
    private void SelectAll(bool select)
    {
        foreach (var i in Items) i.IsSelected = select;
    }

    [RelayCommand]
    private void OpenCookieSettings() => _navigation.NavigateTo(PageKey.Settings, "cookies");
}
