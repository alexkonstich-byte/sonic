using Microsoft.EntityFrameworkCore;
using Sonic.Core.Data;
using Sonic.Core.Models;

namespace Sonic.Core.Repositories;

public interface ITrackRepository
{
    Task<List<Track>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Track?> GetAsync(int id, CancellationToken cancellationToken = default);
    Task<Track?> GetByPathAsync(string path, CancellationToken cancellationToken = default);
    Task<List<Track>> GetRecentAsync(int count, CancellationToken cancellationToken = default);
    Task<List<Track>> GetByKeyAsync(string normalizedKey, CancellationToken cancellationToken = default);
    Task<Dictionary<string, (int Id, DateTime Modified, long Size)>> GetFileIndexAsync(CancellationToken cancellationToken = default);
    Task<Track> UpsertAsync(Track track, CancellationToken cancellationToken = default);
    Task UpsertManyAsync(IReadOnlyCollection<Track> tracks, CancellationToken cancellationToken = default);
    Task SetFavoriteAsync(int id, bool value, CancellationToken cancellationToken = default);
    Task DeleteAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default);
}

public sealed class TrackRepository(IDbContextFactory<SonicDbContext> factory) : ITrackRepository
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task<List<Track>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Tracks.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Track?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Tracks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Track?> GetByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Tracks.AsNoTracking().FirstOrDefaultAsync(t => t.FilePath == path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Track>> GetRecentAsync(int count, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Tracks.AsNoTracking().OrderByDescending(t => t.DateAdded).Take(count).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Track>> GetByKeyAsync(string normalizedKey, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Tracks.AsNoTracking().Where(t => t.NormalizedKey == normalizedKey).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, (int Id, DateTime Modified, long Size)>> GetFileIndexAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Tracks.AsNoTracking()
            .Select(t => new { t.Id, t.FilePath, t.FileModified, t.FileSize })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var index = new Dictionary<string, (int, DateTime, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) index[r.FilePath] = (r.Id, r.FileModified, r.FileSize);
        return index;
    }

    public async Task<Track> UpsertAsync(Track track, CancellationToken cancellationToken = default)
    {
        await UpsertManyAsync([track], cancellationToken).ConfigureAwait(false);
        return track;
    }

    public async Task UpsertManyAsync(IReadOnlyCollection<Track> tracks, CancellationToken cancellationToken = default)
    {
        if (tracks.Count == 0) return;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var paths = tracks.Select(t => t.FilePath).ToList();
            var existing = await db.Tracks.Where(t => paths.Contains(t.FilePath))
                .ToDictionaryAsync(t => t.FilePath, StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);

            foreach (var track in tracks)
            {
                if (existing.TryGetValue(track.FilePath, out var row))
                {
                    // Preserve user-owned state that does not come from the file.
                    track.Id = row.Id;
                    track.IsFavorite = row.IsFavorite;
                    track.DateAdded = row.DateAdded;
                    track.SourceUrl ??= row.SourceUrl;
                    db.Entry(row).CurrentValues.SetValues(track);
                }
                else
                {
                    track.Id = 0;
                    db.Tracks.Add(track);
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SetFavoriteAsync(int id, bool value, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Tracks.Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsFavorite, value), cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            // Keep playlist rows (they retain title/artist), just unlink them.
            await db.PlaylistEntries.Where(e => e.TrackId != null && ids.Contains(e.TrackId.Value))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.TrackId, (int?)null), cancellationToken).ConfigureAwait(false);
            await db.Tracks.Where(t => ids.Contains(t.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
