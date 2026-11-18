using Sonic.Core.Models;

namespace Sonic.Core.Abstractions;

public interface ILibraryService : IDisposable
{
    event EventHandler<LibraryChangedEventArgs>? LibraryChanged;
    event EventHandler<bool>? ScanningChanged;

    bool IsScanning { get; }

    Task<IReadOnlyList<Track>> GetTracksAsync(CancellationToken cancellationToken = default);

    Task<Track?> GetTrackAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Track>> GetRecentlyAddedAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>Scans all configured music folders (plus the download directory).</summary>
    Task<ScanResult> ScanAsync(IProgress<ScanProgress>? progress, CancellationToken cancellationToken = default);

    /// <summary>Imports or refreshes a single file, returning the library entry.</summary>
    Task<Track?> ImportFileAsync(string path, string? sourceUrl = null, CancellationToken cancellationToken = default);

    Task<Track?> FindByKeyAsync(string? artist, string? title, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(CancellationToken cancellationToken = default);

    Task SetFavoriteAsync(int trackId, bool isFavorite, CancellationToken cancellationToken = default);

    /// <summary>Writes tags to the file and refreshes the library entry.</summary>
    Task<Track> UpdateMetadataAsync(int trackId, TrackMetadata metadata, bool removeArtwork, CancellationToken cancellationToken = default);

    /// <summary>Removes entries from the library. Files are moved to the Recycle Bin only when <paramref name="deleteFiles"/> is set.</summary>
    /// <summary>Embeds new artwork (and optionally fills an empty album/year) without touching other tags.</summary>
    Task<Track> ApplyArtworkAsync(int trackId, byte[] artwork, string? album = null, uint? year = null, CancellationToken cancellationToken = default);

    Task RemoveAsync(IReadOnlyCollection<int> trackIds, bool deleteFiles, CancellationToken cancellationToken = default);

    /// <summary>Removes library entries (not files) located under <paramref name="folder"/>.</summary>
    Task RemoveFolderAsync(string folder, CancellationToken cancellationToken = default);

    /// <summary>Starts or stops folder watching according to settings.</summary>
    void ConfigureWatchers();
}
