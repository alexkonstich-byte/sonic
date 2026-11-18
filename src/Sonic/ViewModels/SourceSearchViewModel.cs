using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;
using Sonic.Helpers;

namespace Sonic.ViewModels;

public sealed class SourceResultViewModel(MediaInfo info, double targetSeconds)
{
    public MediaInfo Info { get; } = info;
    public string Title => Info.Title;
    public string? Thumbnail => Info.ThumbnailUrl;
    public string Channel => Info.Uploader ?? "Unknown channel";
    public string DurationText => Format.Duration(Info.DurationSeconds);

    /// <summary>How closely the length matches the playlist entry, which is the best signal of the right recording.</summary>
    public string MatchText
    {
        get
        {
            if (targetSeconds <= 0 || Info.DurationSeconds is not > 0) return string.Empty;
            var diff = Math.Abs(Info.DurationSeconds.Value - targetSeconds);
            return diff <= 3 ? "Length matches" : diff <= 15 ? $"{diff:0}s different" : $"{Format.Duration(diff)} different";
        }
    }

    public bool IsGoodMatch => targetSeconds > 0 && Info.DurationSeconds is > 0 && Math.Abs(Info.DurationSeconds.Value - targetSeconds) <= 3;
}

/// <summary>
/// Looks up candidate recordings on YouTube for a playlist entry imported from Spotify. The user reviews and
/// picks the source; Sonic then downloads it from YouTube (never from Spotify) and applies the Spotify metadata.
/// </summary>
public sealed partial class SourceSearchViewModel(
    IYtDlpService ytDlp,
    IDownloadQueueService queue,
    ILogger<SourceSearchViewModel> logger) : ObservableObject
{
    private PlaylistEntry? _entry;
    private CancellationTokenSource? _cts;

    public ObservableCollection<SourceResultViewModel> Results { get; } = [];

    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private string _entryTitle = string.Empty;
    [ObservableProperty] private string _entryDetails = string.Empty;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string? _message;

    public bool Queued { get; private set; }

    public void Initialize(PlaylistEntry entry)
    {
        _entry = entry;
        EntryTitle = entry.Title;
        EntryDetails = string.Join(" · ", new[] { entry.Artist, entry.Album, entry.DurationSeconds > 0 ? Format.Duration(entry.DurationSeconds) : null }.Where(s => !string.IsNullOrEmpty(s)));
        Query = $"{entry.Artist?.Split(',')[0].Trim()} - {entry.Title}".Trim(' ', '-');
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Query)) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsSearching = true;
        Message = null;
        Results.Clear();
        try
        {
            var results = await Task.Run(() => ytDlp.SearchAsync(Query + " audio", 8, _cts.Token));
            var target = _entry?.DurationSeconds ?? 0;
            foreach (var r in results.OrderByDescending(r => target > 0 && r.DurationSeconds is > 0 && Math.Abs(r.DurationSeconds.Value - target) <= 3))
                Results.Add(new SourceResultViewModel(r, target));
            if (Results.Count == 0) Message = "No results. Try a different search.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is YtDlpException or DependencyMissingException)
        {
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Source search failed");
            Message = "Search failed: " + ex.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>Queues the chosen result with the playlist entry's metadata. Returns true on success.</summary>
    public async Task<bool> ChooseAsync(SourceResultViewModel result)
    {
        if (_entry is null) return false;
        try
        {
            await queue.EnqueueAsync(new DownloadRequest
            {
                Url = result.Info.Url,
                Title = _entry.Title,
                Artist = _entry.Artist,
                ThumbnailUrl = _entry.ArtworkUrl ?? result.Info.ThumbnailUrl,
                DurationSeconds = result.Info.DurationSeconds,
                PlaylistEntryId = _entry.Id,
                Hints = new MetadataHints
                {
                    Title = _entry.Title,
                    Artist = _entry.Artist,
                    Album = _entry.Album,
                    AlbumArtist = _entry.AlbumArtist,
                    TrackNumber = _entry.TrackNumber,
                    Year = _entry.Year,
                    ArtworkUrl = _entry.ArtworkUrl,
                },
            });
            Queued = true;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Message = ex.Message;
            return false;
        }
    }

    public void Cancel() => _cts?.Cancel();
}
