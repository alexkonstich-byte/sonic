using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;
using Sonic.Core.Services;
using Sonic.Helpers;
using Sonic.Services;

namespace Sonic.ViewModels;

public sealed partial class MetadataEditorViewModel(
    ILibraryService library,
    IMetadataService metadata,
    ISpotifyService spotify,
    IWindowService windows,
    IHttpClientFactory httpFactory,
    ICoverArtService coverArt,
    ILogger<MetadataEditorViewModel> logger) : ObservableObject
{
    private const int MaxArtworkBytes = 10 * 1024 * 1024;
    private int _trackId;
    private double? _durationSeconds;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _artist = string.Empty;
    [ObservableProperty] private string _album = string.Empty;
    [ObservableProperty] private string _albumArtist = string.Empty;
    [ObservableProperty] private string _trackNumber = string.Empty;
    [ObservableProperty] private string _year = string.Empty;
    [ObservableProperty] private string _genre = string.Empty;
    [ObservableProperty] private string _comment = string.Empty;
    [ObservableProperty] private byte[]? _artwork;
    [ObservableProperty] private string? _artworkMime;
    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private string _fileDetails = string.Empty;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLookingUp;
    [ObservableProperty] private string? _lookupMessage;

    public ObservableCollection<SpotifyTrack> LookupResults { get; } = [];
    public ObservableCollection<CoverCandidate> CoverResults { get; } = [];

    [ObservableProperty] private bool _isFindingCovers;
    [ObservableProperty] private string? _coverMessage;

    /// <summary>Searches iTunes and Deezer for album covers matching the artist and title fields.</summary>
    [RelayCommand]
    private async Task FindCoversAsync()
    {
        CoverMessage = null;
        CoverResults.Clear();
        if (string.IsNullOrWhiteSpace(Title))
        {
            CoverMessage = "Enter at least a title.";
            return;
        }

        IsFindingCovers = true;
        try
        {
            var results = await coverArt.SearchAsync(NullIfEmpty(Artist), Title, _durationSeconds);
            foreach (var r in results) CoverResults.Add(r);
            CoverMessage = results.Count == 0 ? "No covers found. Check the artist and title." : "Select a cover to use it.";
        }
        finally
        {
            IsFindingCovers = false;
        }
    }

    [RelayCommand]
    private async Task ApplyCoverAsync(CoverCandidate candidate)
    {
        try
        {
            var bytes = await coverArt.DownloadAsync(candidate);
            if (bytes.Length is 0 or >= MaxArtworkBytes) throw new InvalidDataException("The image is empty or too large.");
            Artwork = bytes;
            ArtworkMime = "image/jpeg";
            _artworkChanged = true;
            _artworkRemoved = false;
            if (string.IsNullOrWhiteSpace(Album) && !string.IsNullOrWhiteSpace(candidate.Album)) Album = candidate.Album!;
            if (string.IsNullOrWhiteSpace(Year) && candidate.Year is > 0) Year = candidate.Year.Value.ToString();
            CoverMessage = $"Using the cover of \"{candidate.Album ?? candidate.Title}\". Select Save to write it.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Cover download failed");
            CoverMessage = "The cover couldn't be downloaded: " + ex.Message;
        }
    }
    public bool CanLookup => spotify.IsConfigured;
    public bool Saved { get; private set; }

    private bool _artworkRemoved;

    public async Task<bool> LoadAsync(int trackId)
    {
        _trackId = trackId;
        var track = await library.GetTrackAsync(trackId);
        if (track is null) return false;
        try
        {
            var meta = await Task.Run(() => metadata.Read(track.FilePath, includeArtwork: true));
            Title = meta.Title ?? track.Title;
            Artist = meta.Artist ?? string.Empty;
            Album = meta.Album ?? string.Empty;
            AlbumArtist = meta.AlbumArtist ?? string.Empty;
            TrackNumber = meta.TrackNumber?.ToString() ?? string.Empty;
            Year = meta.Year?.ToString() ?? string.Empty;
            Genre = meta.Genre ?? string.Empty;
            Comment = meta.Comment ?? string.Empty;
            Artwork = meta.Artwork;
            ArtworkMime = meta.ArtworkMimeType;
            _durationSeconds = meta.DurationSeconds > 0 ? meta.DurationSeconds : track.DurationSeconds;
            FileName = Path.GetFileName(track.FilePath);
            FileDetails = $"{track.Extension} · {Format.Duration(meta.DurationSeconds)}{(meta.Bitrate > 0 ? $" · {meta.Bitrate} kbps" : string.Empty)}{(meta.SampleRate > 0 ? $" · {meta.SampleRate / 1000.0:0.#} kHz" : string.Empty)}";
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not read tags from {Path}", track.FilePath);
            Error = "The file's tags couldn't be read: " + ex.Message;
            FileName = Path.GetFileName(track.FilePath);
            Title = track.Title;
            return true;
        }
    }

    /// <summary>Validates and writes tags. Returns false (and sets <see cref="Error"/>) when the dialog should stay open.</summary>
    public async Task<bool> SaveAsync()
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(Title))
        {
            Error = "Enter a title.";
            return false;
        }

        uint? track = null, year = null;
        if (!string.IsNullOrWhiteSpace(TrackNumber))
        {
            if (!uint.TryParse(TrackNumber.Trim(), out var t) || t > 9999)
            {
                Error = "Track number must be a whole number.";
                return false;
            }

            track = t;
        }

        if (!string.IsNullOrWhiteSpace(Year))
        {
            if (!uint.TryParse(Year.Trim(), out var y) || y < 1000 || y > DateTime.Now.Year + 1)
            {
                Error = "Enter a four-digit year.";
                return false;
            }

            year = y;
        }

        IsBusy = true;
        try
        {
            await library.UpdateMetadataAsync(_trackId, new TrackMetadata
            {
                Title = Title.Trim(),
                Artist = NullIfEmpty(Artist),
                Album = NullIfEmpty(Album),
                AlbumArtist = NullIfEmpty(AlbumArtist),
                TrackNumber = track,
                Year = year,
                Genre = NullIfEmpty(Genre),
                Comment = NullIfEmpty(Comment),
                Artwork = _artworkChanged ? Artwork : null,
                ArtworkMimeType = ArtworkMime,
            }, removeArtwork: _artworkRemoved && Artwork is null);
            Saved = true;
            return true;
        }
        catch (IOException ex)
        {
            Error = "The file is in use by another program. Close it and try again. (" + ex.Message + ")";
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Saving tags failed");
            Error = "Couldn't save the tags: " + ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool _artworkChanged;

    [RelayCommand]
    private async Task ChooseArtworkAsync()
    {
        var path = await windows.PickOpenFileAsync(".jpg", ".jpeg", ".png");
        if (path is null) return;
        var info = new FileInfo(path);
        if (info.Length > MaxArtworkBytes)
        {
            Error = "That image is larger than 10 MB. Choose a smaller one.";
            return;
        }

        Artwork = await File.ReadAllBytesAsync(path);
        ArtworkMime = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
        _artworkChanged = true;
        _artworkRemoved = false;
    }

    [RelayCommand]
    private void RemoveArtwork()
    {
        Artwork = null;
        ArtworkMime = null;
        _artworkChanged = false;
        _artworkRemoved = true;
    }

    [RelayCommand]
    private async Task LookupAsync()
    {
        LookupMessage = null;
        LookupResults.Clear();
        var query = string.Join(' ', new[] { Artist, Title }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(query))
        {
            LookupMessage = "Enter at least a title to search.";
            return;
        }

        IsLookingUp = true;
        try
        {
            var results = await spotify.SearchTracksAsync(query, 8);
            foreach (var r in results) LookupResults.Add(r);
            if (results.Count == 0) LookupMessage = "No matches found on Spotify.";
        }
        catch (SpotifyException ex)
        {
            LookupMessage = ex.Message;
        }
        finally
        {
            IsLookingUp = false;
        }
    }

    [RelayCommand]
    private async Task ApplyLookupAsync(SpotifyTrack match)
    {
        Title = match.Name;
        Artist = match.ArtistText;
        Album = match.Album ?? Album;
        AlbumArtist = match.AlbumArtist ?? AlbumArtist;
        TrackNumber = match.TrackNumber?.ToString() ?? TrackNumber;
        Year = match.Year?.ToString() ?? Year;
        LookupResults.Clear();
        LookupMessage = "Applied. Review the fields and select Save.";

        if (match.ImageUrl is null) return;
        try
        {
            var http = httpFactory.CreateClient(DependencyService.HttpClientName);
            var bytes = await http.GetByteArrayAsync(match.ImageUrl);
            if (bytes.Length is > 0 and < MaxArtworkBytes)
            {
                Artwork = bytes;
                ArtworkMime = "image/jpeg";
                _artworkChanged = true;
                _artworkRemoved = false;
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Album art download failed");
            LookupMessage = "Applied tags, but the album art couldn't be downloaded.";
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
