using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;
using Sonic.Helpers;
using Sonic.Services;

namespace Sonic.ViewModels;

public sealed class PlaylistCardViewModel(PlaylistSummary summary)
{
    public PlaylistSummary Model { get; } = summary;
    public int Id => Model.Id;
    public string Name => Model.Name;
    public bool IsSpotify => Model.SourceType == PlaylistSource.Spotify;
    public string? Artwork => Model.ArtworkUrl ?? Model.FirstTrackArtworkPath;
    public string Details
    {
        get
        {
            var parts = new List<string> { Format.Count(Model.EntryCount, "song") };
            if (IsSpotify) parts.Add($"{Model.AvailableCount} in library");
            else if (Model.TotalSeconds > 0) parts.Add(Format.LongDuration(Model.TotalSeconds));
            return string.Join(" · ", parts);
        }
    }
    public string SourceText => IsSpotify ? "Imported from Spotify" : "Playlist";
}

public sealed partial class PlaylistsViewModel : ObservableObject, INavigationAware
{
    private readonly IPlaylistService _playlistService;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly IInAppNotificationService _notifications;
    private readonly ILogger<PlaylistsViewModel> _logger;

    public PlaylistsViewModel(
        IPlaylistService playlists,
        IDialogService dialogs,
        INavigationService navigation,
        IInAppNotificationService notifications,
        IUiDispatcher dispatcher,
        ILogger<PlaylistsViewModel> logger)
    {
        _playlistService = playlists;
        _dialogs = dialogs;
        _navigation = navigation;
        _notifications = notifications;
        _logger = logger;
        playlists.PlaylistsChanged += (_, _) => dispatcher.Post(() => _ = LoadAsync());
    }

    [ObservableProperty] private ObservableCollection<PlaylistCardViewModel> _playlists = [];
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _isLoading;

    public async Task OnNavigatedToAsync(object? parameter) => await LoadAsync();

    public void OnNavigatedFrom() { }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = Playlists.Count == 0;
        try
        {
            var list = await _playlistService.GetPlaylistsAsync();
            Playlists = new ObservableCollection<PlaylistCardViewModel>(list.Select(p => new PlaylistCardViewModel(p)));
            IsEmpty = Playlists.Count == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading playlists failed");
            _notifications.ShowError("Couldn't load playlists", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        var name = await _dialogs.PromptTextAsync("New playlist", "Name", string.Empty, "Create", "My playlist");
        if (name is null) return;
        try
        {
            var p = await _playlistService.CreateAsync(name);
            _navigation.NavigateTo(PageKey.PlaylistDetail, p.Id);
        }
        catch (ArgumentException ex)
        {
            await _dialogs.ShowErrorAsync("Couldn't create the playlist", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportSpotifyAsync()
    {
        var id = await _dialogs.ImportSpotifyAsync(null);
        if (id is { } playlistId) _navigation.NavigateTo(PageKey.PlaylistDetail, playlistId);
    }

    [RelayCommand]
    private void Open(PlaylistCardViewModel playlist) => _navigation.NavigateTo(PageKey.PlaylistDetail, playlist.Id);

    [RelayCommand]
    private async Task RenameAsync(PlaylistCardViewModel playlist)
    {
        var name = await _dialogs.PromptTextAsync("Rename playlist", "Name", playlist.Name, "Rename");
        if (name is null || name == playlist.Name) return;
        try
        {
            await _playlistService.RenameAsync(playlist.Id, name);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await _dialogs.ShowErrorAsync("Couldn't rename the playlist", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(PlaylistCardViewModel playlist)
    {
        if (!await _dialogs.ConfirmAsync("Delete playlist?", $"\"{playlist.Name}\" will be deleted. Songs in your library are not affected.", "Delete", destructive: true))
            return;
        await _playlistService.DeleteAsync(playlist.Id);
    }
}
