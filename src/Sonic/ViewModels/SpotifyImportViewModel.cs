using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;
using Sonic.Helpers;
using Sonic.Services;

namespace Sonic.ViewModels;

public sealed partial class SpotifyImportViewModel(
    ISpotifyService spotify,
    IPlaylistService playlists,
    INavigationService navigation,
    ILogger<SpotifyImportViewModel> logger) : ObservableObject
{
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadPreviewCommand))]
    private string _url = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview), nameof(PreviewDetails), nameof(PreviewTracks))]
    private SpotifyCollection? _preview;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string? _error;

    public bool IsConfigured => spotify.IsConfigured;
    public bool HasPreview => Preview is not null;
    public int? ImportedPlaylistId { get; private set; }

    public string PreviewDetails => Preview is null
        ? string.Empty
        : string.Join(" · ", new[]
        {
            Preview.IsAlbum ? "Album" : "Playlist",
            Preview.Owner,
            Format.Count(Preview.Tracks.Count, "song"),
            Format.LongDuration(Preview.Tracks.Sum(t => t.DurationSeconds)),
            Preview.SkippedItems > 0 ? $"{Preview.SkippedItems} unavailable items skipped" : null,
        }.Where(s => !string.IsNullOrEmpty(s)));

    public IEnumerable<SpotifyTrack> PreviewTracks => Preview?.Tracks.Take(50) ?? [];

    partial void OnUrlChanged(string value)
    {
        Error = null;
        Preview = null;
    }

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadPreviewAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsLoading = true;
        Error = null;
        Preview = null;
        try
        {
            Preview = await spotify.GetCollectionAsync(Url, _cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SpotifyException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Spotify preview failed");
            Error = "Something went wrong: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanLoad() => !string.IsNullOrWhiteSpace(Url);

    /// <summary>Imports the previewed collection. Returns false when the dialog should stay open.</summary>
    public async Task<bool> ImportAsync()
    {
        if (Preview is null)
        {
            await LoadPreviewAsync();
            return false;
        }

        IsImporting = true;
        try
        {
            var playlist = await playlists.ImportSpotifyAsync(Preview);
            ImportedPlaylistId = playlist.Id;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Spotify import failed");
            Error = "The playlist couldn't be saved: " + ex.Message;
            return false;
        }
        finally
        {
            IsImporting = false;
        }
    }

    public void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenSettings() => navigation.NavigateTo(PageKey.Settings, "integrations");
}
