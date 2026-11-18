using Microsoft.EntityFrameworkCore;
using Sonic.Core.Data;
using Sonic.Core.Models;

namespace Sonic.Core.Repositories;

public interface IDownloadRepository
{
    Task<List<DownloadItem>> GetQueueAsync(CancellationToken cancellationToken = default);
    Task<List<DownloadItem>> GetHistoryAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(DownloadItem item, CancellationToken cancellationToken = default);
    Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);
    Task DeleteFinishedHistoryAsync(CancellationToken cancellationToken = default);
}

public sealed class DownloadRepository(IDbContextFactory<SonicDbContext> factory) : IDownloadRepository
{
    public async Task<List<DownloadItem>> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Downloads.AsNoTracking()
            .Where(d => !d.IsDismissed)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<DownloadItem>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var terminal = new[] { DownloadStatus.Completed, DownloadStatus.Failed, DownloadStatus.Canceled, DownloadStatus.Skipped };
        return await db.Downloads.AsNoTracking()
            .Where(d => terminal.Contains(d.Status))
            .OrderByDescending(d => d.CompletedAt ?? d.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertAsync(DownloadItem item, CancellationToken cancellationToken = default)
    {
        var copy = Clone(item);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var existing = await db.Downloads.FirstOrDefaultAsync(d => d.Id == item.Id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                db.Downloads.Add(copy);
            }
            else
            {
                db.Entry(existing).CurrentValues.SetValues(copy);
                existing.Hints = copy.Hints;
                existing.Video = copy.Video;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Downloads.Where(d => ids.Contains(d.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteFinishedHistoryAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var terminal = new[] { DownloadStatus.Completed, DownloadStatus.Failed, DownloadStatus.Canceled, DownloadStatus.Skipped };
        await db.Downloads.Where(d => terminal.Contains(d.Status))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The live item is mutated concurrently by the pipeline; persist a stable snapshot.</summary>
    private static DownloadItem Clone(DownloadItem item)
    {
        var copy = new DownloadItem
        {
            Id = item.Id,
            Url = item.Url,
            Kind = item.Kind,
            Video = item.Video?.Clone(),
            Source = item.Source,
            Title = item.Title,
            Artist = item.Artist,
            ThumbnailUrl = item.ThumbnailUrl,
            DurationSeconds = item.DurationSeconds,
            FormatId = item.FormatId,
            Format = item.Format,
            Quality = item.Quality,
            OutputDirectory = item.OutputDirectory,
            FilenameTemplate = item.FilenameTemplate,
            PlaylistEntryId = item.PlaylistEntryId,
            Status = item.Status,
            Progress = item.Progress,
            TotalBytes = item.TotalBytes,
            DownloadedBytes = item.DownloadedBytes,
            FilePath = item.FilePath,
            ErrorMessage = item.ErrorMessage,
            ErrorDetails = item.ErrorDetails,
            Attempts = item.Attempts,
            IsDismissed = item.IsDismissed,
            CreatedAt = item.CreatedAt,
            StartedAt = item.StartedAt,
            CompletedAt = item.CompletedAt,
        };
        if (item.Hints is { } h)
        {
            copy.Hints = new MetadataHints
            {
                Title = h.Title,
                Artist = h.Artist,
                Album = h.Album,
                AlbumArtist = h.AlbumArtist,
                TrackNumber = h.TrackNumber,
                Year = h.Year,
                Genre = h.Genre,
                ArtworkUrl = h.ArtworkUrl,
            };
        }

        return copy;
    }
}
