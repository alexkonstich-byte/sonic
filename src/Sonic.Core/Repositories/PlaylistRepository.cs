using Microsoft.EntityFrameworkCore;
using Sonic.Core.Abstractions;
using Sonic.Core.Data;
using Sonic.Core.Models;

namespace Sonic.Core.Repositories;

public interface IPlaylistRepository
{
    Task<List<PlaylistSummary>> GetSummariesAsync(CancellationToken cancellationToken = default);
    Task<Playlist?> GetWithEntriesAsync(int id, CancellationToken cancellationToken = default);
    Task<Playlist?> FindExternalAsync(PlaylistSource source, string externalId, CancellationToken cancellationToken = default);
    Task<Playlist> AddAsync(Playlist playlist, CancellationToken cancellationToken = default);
    Task UpdateAsync(int id, Action<Playlist> update, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    Task AddEntriesAsync(int playlistId, IReadOnlyList<PlaylistEntry> entries, CancellationToken cancellationToken = default);
    Task ReplaceEntriesAsync(int playlistId, IReadOnlyList<PlaylistEntry> entries, CancellationToken cancellationToken = default);
    Task RemoveEntriesAsync(int playlistId, IReadOnlyCollection<int> entryIds, CancellationToken cancellationToken = default);
    Task ReorderAsync(int playlistId, IReadOnlyList<int> orderedEntryIds, CancellationToken cancellationToken = default);
    Task SetEntryTrackAsync(int entryId, int? trackId, CancellationToken cancellationToken = default);
    Task<PlaylistEntry?> GetEntryAsync(int entryId, CancellationToken cancellationToken = default);
}

public sealed class PlaylistRepository(IDbContextFactory<SonicDbContext> factory) : IPlaylistRepository
{
    public async Task<List<PlaylistSummary>> GetSummariesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Playlists.AsNoTracking()
            .Select(p => new
            {
                p.Id, p.Name, p.Description, p.SourceType, p.ArtworkUrl, p.UpdatedAt, p.CreatedAt,
                Count = p.Entries.Count,
                Available = p.Entries.Count(e => e.TrackId != null),
                Seconds = p.Entries.Sum(e => e.Track != null ? e.Track.DurationSeconds : e.DurationSeconds),
                FirstArt = p.Entries.Where(e => e.Track != null && e.Track.ArtworkPath != null)
                    .OrderBy(e => e.Position).Select(e => e.Track!.ArtworkPath).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows
            .OrderByDescending(r => r.UpdatedAt)
            .Select(r => new PlaylistSummary(r.Id, r.Name, r.Description, r.SourceType, r.ArtworkUrl, r.FirstArt,
                r.Count, r.Available, r.Seconds, r.UpdatedAt, r.CreatedAt))
            .ToList();
    }

    public async Task<Playlist?> GetWithEntriesAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var playlist = await db.Playlists.AsNoTracking()
            .Include(p => p.Entries).ThenInclude(e => e.Track)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false);
        if (playlist is not null) playlist.Entries = playlist.Entries.OrderBy(e => e.Position).ToList();
        return playlist;
    }

    public async Task<Playlist?> FindExternalAsync(PlaylistSource source, string externalId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Playlists.AsNoTracking()
            .FirstOrDefaultAsync(p => p.SourceType == source && p.ExternalId == externalId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Playlist> AddAsync(Playlist playlist, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return playlist;
    }

    public async Task UpdateAsync(int id, Action<Playlist> update, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var p = await db.Playlists.FirstOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The playlist no longer exists.");
        update(p);
        p.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.PlaylistEntries.Where(e => e.PlaylistId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Playlists.Where(p => p.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddEntriesAsync(int playlistId, IReadOnlyList<PlaylistEntry> entries, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var max = await db.PlaylistEntries.Where(e => e.PlaylistId == playlistId)
            .MaxAsync(e => (int?)e.Position, cancellationToken).ConfigureAwait(false) ?? -1;
        foreach (var e in entries)
        {
            e.Id = 0;
            e.PlaylistId = playlistId;
            e.Position = ++max;
            e.Track = null;
            db.PlaylistEntries.Add(e);
        }

        await TouchAsync(db, playlistId, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceEntriesAsync(int playlistId, IReadOnlyList<PlaylistEntry> entries, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.PlaylistEntries.Where(e => e.PlaylistId == playlistId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var pos = 0;
        foreach (var e in entries)
        {
            e.Id = 0;
            e.PlaylistId = playlistId;
            e.Position = pos++;
            e.Track = null;
            db.PlaylistEntries.Add(e);
        }

        await TouchAsync(db, playlistId, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveEntriesAsync(int playlistId, IReadOnlyCollection<int> entryIds, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.PlaylistEntries.Where(e => e.PlaylistId == playlistId && entryIds.Contains(e.Id))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await TouchAsync(db, playlistId, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReorderAsync(int playlistId, IReadOnlyList<int> orderedEntryIds, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entries = await db.PlaylistEntries.Where(e => e.PlaylistId == playlistId)
            .ToDictionaryAsync(e => e.Id, cancellationToken).ConfigureAwait(false);
        var pos = 0;
        foreach (var id in orderedEntryIds)
        {
            if (entries.Remove(id, out var entry)) entry.Position = pos++;
        }

        foreach (var leftover in entries.Values.OrderBy(e => e.Position)) leftover.Position = pos++;
        await TouchAsync(db, playlistId, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetEntryTrackAsync(int entryId, int? trackId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.PlaylistEntries.Where(e => e.Id == entryId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.TrackId, trackId), cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlaylistEntry?> GetEntryAsync(int entryId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.PlaylistEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task TouchAsync(SonicDbContext db, int playlistId, CancellationToken ct)
    {
        var p = await db.Playlists.FirstOrDefaultAsync(x => x.Id == playlistId, ct).ConfigureAwait(false);
        if (p is not null) p.UpdatedAt = DateTime.UtcNow;
    }
}
