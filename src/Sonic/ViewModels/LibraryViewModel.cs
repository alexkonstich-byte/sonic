using Sonic.Core.Helpers;
using Sonic.Core.Services;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;
using Sonic.Helpers;
using Sonic.Services;

namespace Sonic.ViewModels;

public sealed partial class TrackViewModel(Track track) : ObservableObject
{
    public Track Model { get; private set; } = track;
    public int Id => Model.Id;
    public string Title => Model.Title;
    public string Artist => Model.DisplayArtist;
    public string Album => Model.DisplayAlbum;
    public string? ArtworkPath => Model.ArtworkPath;
    public string DurationText => Format.Duration(Model.DurationSeconds);
    public string YearText => Model.Year?.ToString() ?? string.Empty;
    public string FormatText => Model.Extension;
    public string Details => $"{Model.Extension}{(Model.Bitrate > 0 ? $" · {Model.Bitrate} kbps" : string.Empty)} · {Format.Bytes(Model.FileSize)}";
    public string DateAddedText => Format.RelativeTime(Model.DateAdded);

    [ObservableProperty] private bool _isFavorite = track.IsFavorite;

    public string FavoriteGlyph => IsFavorite ? Glyphs.FavoriteFilled : Glyphs.Favorite;
    partial void OnIsFavoriteChanged(bool value) => OnPropertyChanged(nameof(FavoriteGlyph));
}

public sealed class AlbumViewModel(string name, string artist, IReadOnlyList<Track> tracks)
{
    public string Name { get; } = name;
    public string Artist { get; } = artist;
    public IReadOnlyList<Track> Tracks { get; } = tracks;
    public string? ArtworkPath => Tracks.Select(t => t.ArtworkPath).FirstOrDefault(p => p is not null);
    public string Details => string.Join(" · ", new[] { Tracks.Select(t => t.Year).FirstOrDefault(y => y is not null)?.ToString(), Format.Count(Tracks.Count, "song") }.Where(s => s is not null));
}

public sealed class ArtistViewModel(string name, IReadOnlyList<Track> tracks)
{
    public string Name { get; } = name;
    public IReadOnlyList<Track> Tracks { get; } = tracks;
    public string? ArtworkPath => Tracks.Select(t => t.ArtworkPath).FirstOrDefault(p => p is not null);
    public string Details
    {
        get
        {
            var albums = Tracks.Select(t => t.Album).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.CurrentCultureIgnoreCase).Count();
            return albums > 0 ? $"{Format.Count(albums, "album")} · {Format.Count(Tracks.Count, "song")}" : Format.Count(Tracks.Count, "song");
        }
    }
}

public sealed class DuplicateGroupViewModel(DuplicateGroup group)
{
    public string Title => group.Tracks[0].Title;
    public string Artist => group.Tracks[0].DisplayArtist;
    public IReadOnlyList<TrackViewModel> Tracks { get; } = group.Tracks.Select(t => new TrackViewModel(t)).ToList();
    public string Summary => $"{Format.Count(group.Tracks.Count, "copy", "copies")}";
}

public enum LibraryView
{
    Songs,
    Albums,
    Artists,
    Favorites,
    Recent,
    Duplicates,
}

public sealed partial class LibraryViewModel : ObservableObject, INavigationAware
{
    private readonly ILibraryService _library;
    private readonly IPlaylistService _playlists;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IShellLauncher _launcher;
    private readonly IWindowService _windows;
    private readonly IInAppNotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<LibraryViewModel> _logger;
    private List<Track> _all = [];
    private bool _loaded;
    private bool _isVisible;
    private bool _reloadPending;
    private CancellationTokenSource? _searchDebounce;

    public LibraryViewModel(
        ILibraryService library,
        IPlaylistService playlists,
        ISettingsService settings,
        IDialogService dialogs,
        IShellLauncher launcher,
        IWindowService windows,
        IInAppNotificationService notifications,
        IUiDispatcher dispatcher,
        ILogger<LibraryViewModel> logger)
    {
        _library = library;
        _playlists = playlists;
        _settings = settings;
        _dialogs = dialogs;
        _launcher = launcher;
        _windows = windows;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _logger = logger;
        _isScanning = library.IsScanning;
        library.LibraryChanged += (_, _) => dispatcher.Post(() =>
        {
            if (_isVisible) _ = LoadAsync();
            else _reloadPending = true;
        });
        library.ScanningChanged += (_, scanning) => dispatcher.Post(() => IsScanning = scanning);
    }

    public IReadOnlyList<string> SortOptions { get; } = ["Title", "Artist", "Album", "Date added", "Duration", "Year"];
    public IReadOnlyList<string> FormatFilters { get; } = ["All formats", "MP3", "FLAC", "M4A", "OPUS", "OGG", "WAV", "Other"];

    [ObservableProperty] private ObservableCollection<TrackViewModel> _tracks = [];
    [ObservableProperty] private ObservableCollection<AlbumViewModel> _albums = [];
    [ObservableProperty] private ObservableCollection<ArtistViewModel> _artists = [];
    [ObservableProperty] private ObservableCollection<DuplicateGroupViewModel> _duplicates = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrackView), nameof(IsAlbumView), nameof(IsArtistView), nameof(IsDuplicateView))]
    private LibraryView _currentView = LibraryView.Songs;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _sortOption = "Title";
    [ObservableProperty] private bool _sortDescending;
    [ObservableProperty] private string _formatFilter = "All formats";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScopeFilter))]
    private string? _scopeFilterText;

    private Func<Track, bool>? _scopeFilter;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanStatus = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoResults))]
    private bool _isLibraryEmpty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoResults))]
    private bool _isResultEmpty;

    public bool ShowNoResults => IsResultEmpty && !IsLibraryEmpty;
    [ObservableProperty] private int _selectedCount;

    public List<TrackViewModel> SelectedTracks { get; } = [];

    public bool IsTrackView => CurrentView is LibraryView.Songs or LibraryView.Favorites or LibraryView.Recent;
    public bool IsAlbumView => CurrentView == LibraryView.Albums;
    public bool IsArtistView => CurrentView == LibraryView.Artists;
    public bool IsDuplicateView => CurrentView == LibraryView.Duplicates;
    public bool HasScopeFilter => ScopeFilterText is not null;

    partial void OnCurrentViewChanged(LibraryView value)
    {
        if (value is not (LibraryView.Songs)) ClearScopeCore();
        _ = ApplyAsync();
    }

    partial void OnSortOptionChanged(string value) => _ = ApplyAsync();
    partial void OnSortDescendingChanged(bool value) => _ = ApplyAsync();
    partial void OnFormatFilterChanged(string value) => _ = ApplyAsync();

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce?.Cancel();
        _searchDebounce = new CancellationTokenSource();
        var token = _searchDebounce.Token;
        _ = Task.Delay(200, token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _dispatcher.Post(() => _ = ApplyAsync());
        }, TaskScheduler.Default);
    }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        _isVisible = true;
        if (!_loaded || _reloadPending) await LoadAsync();
    }

    public void OnNavigatedFrom() => _isVisible = false;

    [RelayCommand]
    private async Task LoadAsync()
    {
        _reloadPending = false;
        IsLoading = !_loaded;
        try
        {
            _all = (await _library.GetTracksAsync()).ToList();
            _loaded = true;
            IsLibraryEmpty = _all.Count == 0;
            var albums = _all.Select(t => (t.Album ?? string.Empty).ToLowerInvariant() + "|" + t.GroupingArtist.ToLowerInvariant())
                .Where(a => !a.StartsWith('|')).Distinct().Count();
            Summary = $"{Format.Count(_all.Count, "song")} · {Format.Count(albums, "album")} · {Format.Bytes(_all.Sum(t => t.FileSize))}";
            await ApplyAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading library failed");
            _notifications.ShowError("Couldn't load the library", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ApplyAsync()
    {
        if (!_loaded) return;
        var query = SearchText.Trim();
        var view = CurrentView;
        var sort = SortOption;
        var desc = SortDescending;
        var format = FormatFilter;
        var scope = _scopeFilter;
        var source = _all;

        if (view == LibraryView.Duplicates)
        {
            var groups = await _library.FindDuplicatesAsync();
            Duplicates = new ObservableCollection<DuplicateGroupViewModel>(groups.Select(g => new DuplicateGroupViewModel(g)));
            IsResultEmpty = Duplicates.Count == 0;
            return;
        }

        // Filtering/sorting large libraries happens off the UI thread.
        var filtered = await Task.Run(() =>
        {
            IEnumerable<Track> items = source;
            if (scope is not null) items = items.Where(scope);
            if (view == LibraryView.Favorites) items = items.Where(t => t.IsFavorite);
            if (format != "All formats")
            {
                string[] known = ["MP3", "FLAC", "M4A", "OPUS", "OGG", "WAV"];
                items = format == "Other"
                    ? items.Where(t => !known.Contains(t.Extension, StringComparer.OrdinalIgnoreCase))
                    : items.Where(t => string.Equals(t.Extension, format, StringComparison.OrdinalIgnoreCase));
            }

            if (query.Length > 0)
            {
                items = items.Where(t => t.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                         || (t.Artist?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
                                         || (t.Album?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
                                         || (t.AlbumArtist?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
                                         || (t.Genre?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false));
            }

            if (view == LibraryView.Recent)
                return items.OrderByDescending(t => t.DateAdded).Take(200).ToList();

            var cmp = StringComparer.CurrentCultureIgnoreCase;
            IOrderedEnumerable<Track> ordered = sort switch
            {
                "Artist" => desc ? items.OrderByDescending(t => t.DisplayArtist, cmp) : items.OrderBy(t => t.DisplayArtist, cmp),
                "Album" => desc ? items.OrderByDescending(t => t.DisplayAlbum, cmp) : items.OrderBy(t => t.DisplayAlbum, cmp),
                "Date added" => desc ? items.OrderBy(t => t.DateAdded) : items.OrderByDescending(t => t.DateAdded),
                "Duration" => desc ? items.OrderByDescending(t => t.DurationSeconds) : items.OrderBy(t => t.DurationSeconds),
                "Year" => desc ? items.OrderByDescending(t => t.Year ?? 0) : items.OrderBy(t => t.Year ?? 0),
                _ => desc ? items.OrderByDescending(t => t.Title, cmp) : items.OrderBy(t => t.Title, cmp),
            };
            return ordered.ThenBy(t => t.Album, cmp).ThenBy(t => t.TrackNumber ?? 0).ThenBy(t => t.Title, cmp).ToList();
        });

        switch (view)
        {
            case LibraryView.Albums:
                Albums = new ObservableCollection<AlbumViewModel>(filtered
                    .GroupBy(t => (Album: t.DisplayAlbum, Artist: t.GroupingArtist), new AlbumKeyComparer())
                    .OrderBy(g => g.Key.Album, StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => new AlbumViewModel(g.Key.Album, g.Key.Artist, g.OrderBy(t => t.TrackNumber ?? 0).ToList())));
                IsResultEmpty = Albums.Count == 0;
                break;
            case LibraryView.Artists:
                Artists = new ObservableCollection<ArtistViewModel>(filtered
                    .GroupBy(t => t.GroupingArtist, StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => new ArtistViewModel(g.Key, g.ToList())));
                IsResultEmpty = Artists.Count == 0;
                break;
            default:
                Tracks = new ObservableCollection<TrackViewModel>(filtered.Select(t => new TrackViewModel(t)));
                IsResultEmpty = Tracks.Count == 0;
                break;
        }
    }

    private sealed class AlbumKeyComparer : IEqualityComparer<(string Album, string Artist)>
    {
        public bool Equals((string Album, string Artist) x, (string Album, string Artist) y) =>
            string.Equals(x.Album, y.Album, StringComparison.CurrentCultureIgnoreCase) && string.Equals(x.Artist, y.Artist, StringComparison.CurrentCultureIgnoreCase);

        public int GetHashCode((string Album, string Artist) obj) =>
            HashCode.Combine(obj.Album.ToLowerInvariant(), obj.Artist.ToLowerInvariant());
    }

    public void UpdateSelection(IEnumerable<TrackViewModel> selected)
    {
        SelectedTracks.Clear();
        SelectedTracks.AddRange(selected);
        SelectedCount = SelectedTracks.Count;
    }

    [RelayCommand]
    private void OpenAlbum(AlbumViewModel album)
    {
        var name = album.Name;
        var artist = album.Artist;
        SetScope($"Album: {name}", t => string.Equals(t.DisplayAlbum, name, StringComparison.CurrentCultureIgnoreCase)
                                       && string.Equals(t.GroupingArtist, artist, StringComparison.CurrentCultureIgnoreCase));
        SortOption = "Album";
    }

    [RelayCommand]
    private void OpenArtist(ArtistViewModel artist)
    {
        var name = artist.Name;
        SetScope($"Artist: {name}", t => string.Equals(t.GroupingArtist, name, StringComparison.CurrentCultureIgnoreCase));
    }

    private void SetScope(string text, Func<Track, bool> predicate)
    {
        _scopeFilter = predicate;
        ScopeFilterText = text;
        if (CurrentView != LibraryView.Songs) CurrentView = LibraryView.Songs;
        else _ = ApplyAsync();
    }

    [RelayCommand]
    private void ClearScope()
    {
        ClearScopeCore();
        _ = ApplyAsync();
    }

    private void ClearScopeCore()
    {
        _scopeFilter = null;
        ScopeFilterText = null;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (_library.IsScanning) return;
        ScanStatus = "Scanning…";
        try
        {
            var progress = new Progress<ScanProgress>(p => ScanStatus = $"Scanning {p.Processed:N0} of {p.Total:N0}");
            var result = await Task.Run(() => _library.ScanAsync(progress));
            _notifications.ShowSuccess("Library updated", $"{result.Added} added, {result.Updated} updated, {result.Removed} removed" + (result.Failed > 0 ? $", {result.Failed} couldn't be read." : "."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed");
            _notifications.ShowError("Scan failed", ex.Message);
        }
        finally
        {
            ScanStatus = string.Empty;
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var folder = await _windows.PickFolderAsync();
        if (folder is null) return;
        if (_settings.Current.MusicFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            _notifications.ShowInfo("Already added", $"\"{folder}\" is already part of your library.");
            return;
        }

        await _settings.UpdateAsync(s => s.MusicFolders.Add(folder));
        _library.ConfigureWatchers();
        await ScanAsync();
    }

    /// <summary>Plays in Sonic's player with the current view (search/sort applied) as the queue.</summary>
    [RelayCommand]
    private Task Play(TrackViewModel track) =>
        App.GetService<PlayerViewModel>().PlayTrackAsync(track.Model, Tracks.Contains(track) ? Tracks.Select(t => t.Model).ToList() : null);

    [RelayCommand]
    private void AddToQueue(TrackViewModel? track) =>
        App.GetService<PlayerViewModel>().AddToQueue(Targets(track).Select(t => t.Model));

    [RelayCommand]
    private void OpenExternal(TrackViewModel track) => _launcher.OpenFile(track.Model.FilePath);

    [RelayCommand]
    private void OpenLocation(TrackViewModel track) => _launcher.RevealInExplorer(track.Model.FilePath);

    [RelayCommand]
    private async Task EditMetadataAsync(TrackViewModel track)
    {
        if (!File.Exists(track.Model.FilePath))
        {
            await _dialogs.ShowErrorAsync("File not found", $"\"{track.Model.FilePath}\" no longer exists. Rescan the library to remove missing files.");
            return;
        }

        await _dialogs.EditMetadataAsync(track.Id);
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(TrackViewModel track)
    {
        var value = !track.IsFavorite;
        track.IsFavorite = value;
        track.Model.IsFavorite = value;
        try
        {
            await _library.SetFavoriteAsync(track.Id, value);
        }
        catch (Exception ex)
        {
            track.IsFavorite = !value;
            track.Model.IsFavorite = !value;
            _notifications.ShowError("Couldn't update favorite", ex.Message);
        }
    }

    private IReadOnlyList<TrackViewModel> Targets(TrackViewModel? track) =>
        track is not null && !SelectedTracks.Contains(track) ? [track] : SelectedTracks.Count > 0 ? SelectedTracks.ToList() : track is null ? [] : [track];

    private bool _findingCovers;

    /// <summary>Looks up album covers for the selected songs and applies confident matches.</summary>
    [RelayCommand]
    private async Task FindCoversAsync(TrackViewModel? track)
    {
        var targets = Targets(track);
        if (targets.Count == 0) return;
        if (_findingCovers)
        {
            _notifications.ShowInfo("Already searching", "Album covers are being looked up.");
            return;
        }

        _findingCovers = true;
        var coverArt = App.GetService<ICoverArtService>();
        int updated = 0, unsure = 0, failed = 0;
        _notifications.ShowInfo("Finding album covers", $"Looking up {Format.Count(targets.Count, "song")} on iTunes and Deezer…");
        try
        {
            foreach (var t in targets)
            {
                try
                {
                    var artist = t.Model.Artist;
                    var title = t.Model.Title;
                    if (string.IsNullOrWhiteSpace(artist) && TitleParser.TrySplitArtistTitle(title, out var a, out var ti))
                    {
                        artist = a;
                        title = ti;
                    }

                    var results = await coverArt.SearchAsync(artist, title, t.Model.DurationSeconds);
                    if (results.FirstOrDefault() is not { Score: >= CoverArtService.ConfidentScore } best)
                    {
                        unsure++;
                        continue;
                    }

                    var bytes = await coverArt.DownloadAsync(best);
                    await _library.ApplyArtworkAsync(t.Id, bytes, best.Album, best.Year is > 0 ? (uint)best.Year.Value : null);
                    updated++;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or TaskCanceledException)
                {
                    _logger.LogWarning(ex, "Cover lookup failed for {Path}", t.Model.FilePath);
                    failed++;
                }

                // Be gentle with the public APIs on big selections.
                if (targets.Count > 1) await Task.Delay(350);
            }
        }
        finally
        {
            _findingCovers = false;
        }

        var details = new List<string> { $"{updated} updated" };
        if (unsure > 0) details.Add($"{unsure} without a confident match (use Edit metadata › Find cover online)");
        if (failed > 0) details.Add($"{failed} failed");
        if (updated > 0) _notifications.ShowSuccess("Album covers updated", string.Join(", ", details) + ".");
        else _notifications.ShowWarning("No covers applied", string.Join(", ", details) + ".");
    }

    [RelayCommand]
    private async Task RemoveFromLibraryAsync(TrackViewModel? track)
    {
        var targets = Targets(track);
        if (targets.Count == 0) return;
        var what = targets.Count == 1 ? $"\"{targets[0].Title}\"" : Format.Count(targets.Count, "song");
        if (!await _dialogs.ConfirmAsync("Remove from library?", $"{what} will be removed from Sonic's library. The files stay on your disk (they'll come back on the next scan if they're inside a library folder).", "Remove"))
            return;
        await _library.RemoveAsync(targets.Select(t => t.Id).ToList(), deleteFiles: false);
    }

    [RelayCommand]
    private async Task DeleteFilesAsync(TrackViewModel? track)
    {
        var targets = Targets(track);
        if (targets.Count == 0) return;
        var what = targets.Count == 1 ? $"\"{targets[0].Title}\"" : Format.Count(targets.Count, "file");
        if (!await _dialogs.ConfirmAsync("Delete files?", $"{what} will be moved to the Recycle Bin and removed from the library.", "Delete", destructive: true))
            return;
        try
        {
            await _library.RemoveAsync(targets.Select(t => t.Id).ToList(), deleteFiles: true);
            _notifications.ShowSuccess("Moved to Recycle Bin", what);
        }
        catch (AggregateException ex)
        {
            await _dialogs.ShowErrorAsync("Some files couldn't be deleted", string.Join("\n", ex.InnerExceptions.Select(e => e.Message)));
        }
    }

    public async Task<IReadOnlyList<PlaylistSummary>> GetLocalPlaylistsAsync() =>
        (await _playlists.GetPlaylistsAsync()).Where(p => p.SourceType == PlaylistSource.Local).ToList();

    public async Task AddToPlaylistAsync(int playlistId, string playlistName, TrackViewModel? track)
    {
        var targets = Targets(track);
        if (targets.Count == 0) return;
        try
        {
            await _playlists.AddTracksAsync(playlistId, targets.Select(t => t.Id).ToList());
            _notifications.ShowSuccess("Added to playlist", $"{Format.Count(targets.Count, "song")} added to \"{playlistName}\".");
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Couldn't add to playlist", ex.Message);
        }
    }

    [RelayCommand]
    private async Task AddToNewPlaylistAsync(TrackViewModel? track)
    {
        var targets = Targets(track);
        var name = await _dialogs.PromptTextAsync("New playlist", "Name", string.Empty, "Create", "My playlist");
        if (name is null) return;
        try
        {
            var playlist = await _playlists.CreateAsync(name);
            if (targets.Count > 0) await AddToPlaylistAsync(playlist.Id, playlist.Name, track);
        }
        catch (ArgumentException ex)
        {
            await _dialogs.ShowErrorAsync("Couldn't create the playlist", ex.Message);
        }
    }

    [RelayCommand]
    private void ShowView(string view)
    {
        if (Enum.TryParse<LibraryView>(view, out var v)) CurrentView = v;
    }
}
