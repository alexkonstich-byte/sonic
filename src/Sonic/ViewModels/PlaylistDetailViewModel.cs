using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;
using Sonic.Helpers;
using Sonic.Services;

namespace Sonic.ViewModels;

public sealed partial class PlaylistEntryViewModel(PlaylistEntry entry, int index) : ObservableObject
{
    public PlaylistEntry Model { get; } = entry;
    public int Id => Model.Id;

    [ObservableProperty] private int _index = index;

    public string Title => Model.Track?.Title ?? Model.Title;
    public string Artist => Model.Track?.DisplayArtist ?? Model.Artist ?? "Unknown artist";
    public string Album => Model.Track?.Album ?? Model.Album ?? string.Empty;
    public string? Artwork => Model.Track?.ArtworkPath ?? Model.ArtworkUrl;
    public string DurationText => Format.Duration(Model.Track?.DurationSeconds ?? Model.DurationSeconds);
    public bool IsAvailable => Model.Track is not null && File.Exists(Model.Track.FilePath);
    public bool IsMissing => !IsAvailable;
}

public sealed partial class PlaylistDetailViewModel : ObservableObject, INavigationAware
{
    private readonly IPlaylistService _playlists;
    private readonly ISpotifyService _spotify;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly IShellLauncher _launcher;
    private readonly IWindowService _windows;
    private readonly IInAppNotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<PlaylistDetailViewModel> _logger;
    private int _playlistId;
    private bool _suppressReload;

    public PlaylistDetailViewModel(
        IPlaylistService playlists,
        ISpotifyService spotify,
        IDialogService dialogs,
        INavigationService navigation,
        IShellLauncher launcher,
        IWindowService windows,
        IInAppNotificationService notifications,
        IUiDispatcher dispatcher,
        ILogger<PlaylistDetailViewModel> logger)
    {
        _playlists = playlists;
        _spotify = spotify;
        _dialogs = dialogs;
        _navigation = navigation;
        _launcher = launcher;
        _windows = windows;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public ObservableCollection<PlaylistEntryViewModel> Entries { get; } = [];

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string? _artwork;
    [ObservableProperty] private string _details = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSpotifyHint))]
    private bool _isSpotify;

    [ObservableProperty] private string? _externalUrl;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSpotifyHint))]
    private int _missingCount;

    public bool ShowSpotifyHint => IsSpotify && MissingCount > 0;

    public string SourceLabel => IsSpotify ? "Spotify playlist" : "Playlist";

    public async Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is int id) _playlistId = id;
        _playlists.PlaylistsChanged += OnPlaylistsChanged;
        await LoadAsync();
    }

    public void OnNavigatedFrom() => _playlists.PlaylistsChanged -= OnPlaylistsChanged;

    private void OnPlaylistsChanged(object? sender, EventArgs e)
    {
        if (_suppressReload) return;
        _dispatcher.Post(() => _ = LoadAsync());
    }

    private async Task LoadAsync()
    {
        try
        {
            var p = await _playlists.GetPlaylistAsync(_playlistId);
            if (p is null)
            {
                _navigation.NavigateTo(PageKey.Playlists);
                return;
            }

            Name = p.Name;
            Description = p.Description;
            IsSpotify = p.SourceType == PlaylistSource.Spotify;
            ExternalUrl = p.ExternalUrl;
            OnPropertyChanged(nameof(SourceLabel));

            Entries.Clear();
            var i = 1;
            foreach (var e in p.Entries) Entries.Add(new PlaylistEntryViewModel(e, i++));
            Artwork = p.ArtworkUrl ?? Entries.Select(e => e.Artwork).FirstOrDefault(a => a is not null);
            MissingCount = Entries.Count(e => e.IsMissing);
            IsEmpty = Entries.Count == 0;
            var total = p.Entries.Sum(e => e.Track?.DurationSeconds ?? e.DurationSeconds);
            var parts = new List<string> { Format.Count(Entries.Count, "song") };
            if (total > 0) parts.Add(Format.LongDuration(total));
            if (IsSpotify) parts.Add($"{Entries.Count - MissingCount} in library");
            if (p.Owner is not null) parts.Insert(0, p.Owner);
            Details = string.Join(" · ", parts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading playlist failed");
            _notifications.ShowError("Couldn't load the playlist", ex.Message);
        }
    }

    [RelayCommand]
    private void Back() => _navigation.NavigateTo(PageKey.Playlists);

    /// <summary>Plays the playlist in Sonic's player, starting at the entry; songs not in the library are skipped.</summary>
    [RelayCommand]
    private Task PlayEntry(PlaylistEntryViewModel entry)
    {
        var tracks = Entries.Where(e => e.IsAvailable).Select(e => e.Model.Track!).ToList();
        return entry.Model.Track is { } t ? App.GetService<PlayerViewModel>().PlayTrackAsync(t, tracks) : Task.CompletedTask;
    }

    [RelayCommand]
    private Task PlayAll()
    {
        var tracks = Entries.Where(e => e.IsAvailable).Select(e => e.Model.Track!).ToList();
        if (tracks.Count == 0)
        {
            _notifications.ShowInfo("Nothing to play", "None of these songs are in your library yet.");
            return Task.CompletedTask;
        }

        return App.GetService<PlayerViewModel>().PlayAsync(tracks);
    }

    [RelayCommand]
    private void RevealEntry(PlaylistEntryViewModel entry)
    {
        if (entry.Model.Track is { } t) _launcher.RevealInExplorer(t.FilePath);
    }

    [RelayCommand]
    private async Task FindSourceAsync(PlaylistEntryViewModel entry)
    {
        if (await _dialogs.FindSourceAsync(entry.Model))
            _notifications.ShowSuccess("Added to downloads", $"\"{entry.Title}\" will be linked to this playlist when it finishes.");
    }

    [RelayCommand]
    private async Task RemoveEntryAsync(PlaylistEntryViewModel entry)
    {
        await _playlists.RemoveEntriesAsync(_playlistId, [entry.Id]);
    }

    /// <summary>Persists the order after a drag-and-drop reorder in the list.</summary>
    public async Task PersistOrderAsync()
    {
        var i = 1;
        foreach (var e in Entries) e.Index = i++;
        _suppressReload = true;
        try
        {
            await _playlists.ReorderAsync(_playlistId, Entries.Select(e => e.Id).ToList());
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Couldn't save the new order", ex.Message);
        }
        finally
        {
            _suppressReload = false;
        }
    }

    /// <summary>Downloads every song that isn't in the chosen folder or the library yet.</summary>
    [RelayCommand]
    private async Task DownloadAllAsync()
    {
        var queued = await _dialogs.PlaylistDownloadAsync(_playlistId, Name);
        if (queued > 0) _navigation.NavigateTo(PageKey.Downloads);
    }

    [RelayCommand]
    private async Task MatchLibraryAsync()
    {
        IsBusy = true;
        try
        {
            var matched = await _playlists.MatchWithLibraryAsync(_playlistId);
            _notifications.ShowInfo("Library match", matched == 0 ? "No new matches were found in your library." : $"{Format.Count(matched, "song")} matched to files in your library.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshFromSpotifyAsync()
    {
        if (ExternalUrl is null) return;
        IsBusy = true;
        try
        {
            var collection = await _spotify.GetCollectionAsync(ExternalUrl);
            await _playlists.ImportSpotifyAsync(collection);
            _notifications.ShowSuccess("Playlist refreshed", $"\"{collection.Name}\" is up to date with Spotify.");
        }
        catch (SpotifyException ex)
        {
            await _dialogs.ShowErrorAsync("Couldn't refresh from Spotify", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenInSpotify()
    {
        if (ExternalUrl is not null) _launcher.OpenUri(ExternalUrl);
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = await _windows.PickSaveFileAsync(FileNameHelper.SanitizeSegment(Name, "Playlist"), "M3U playlist", ".m3u8");
        if (path is null) return;
        try
        {
            await _playlists.ExportM3uAsync(_playlistId, path);
            var skipped = MissingCount;
            _notifications.ShowSuccess("Playlist exported", skipped > 0 ? $"Saved to {Path.GetFileName(path)}. {skipped} songs not in your library were left out." : $"Saved to {Path.GetFileName(path)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync("Export failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        var name = await _dialogs.PromptTextAsync("Rename playlist", "Name", Name, "Rename");
        if (name is null || name == Name) return;
        try
        {
            await _playlists.RenameAsync(_playlistId, name);
        }
        catch (ArgumentException ex)
        {
            await _dialogs.ShowErrorAsync("Couldn't rename the playlist", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (!await _dialogs.ConfirmAsync("Delete playlist?", $"\"{Name}\" will be deleted. Songs in your library are not affected.", "Delete", destructive: true))
            return;
        await _playlists.DeleteAsync(_playlistId);
        _navigation.NavigateTo(PageKey.Playlists);
    }

    [RelayCommand]
    private void GoToLibrary() => _navigation.NavigateTo(PageKey.Library);
}
