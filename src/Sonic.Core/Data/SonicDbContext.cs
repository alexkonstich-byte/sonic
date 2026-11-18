using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sonic.Core.Models;

namespace Sonic.Core.Data;

public sealed class SonicDbContext(DbContextOptions<SonicDbContext> options) : DbContext(options)
{
    public DbSet<DownloadItem> Downloads => Set<DownloadItem>();
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<Playlist> Playlists => Set<Playlist>();
    public DbSet<PlaylistEntry> PlaylistEntries => Set<PlaylistEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DownloadItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Url).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Format).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Quality).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(24);
            e.OwnsOne(x => x.Hints);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(8).HasDefaultValue(MediaKind.Audio).HasSentinel((MediaKind)(-1));
            e.OwnsOne(x => x.Video, v =>
            {
                v.Property(p => p.Container).HasConversion<string>().HasMaxLength(8);
                v.Property(p => p.Codec).HasConversion<string>().HasMaxLength(16);
                v.Property(p => p.SubtitleMode).HasConversion<string>().HasMaxLength(12);
                v.Property(p => p.SubtitleFormat).HasConversion<string>().HasMaxLength(8);
            });
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<Track>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FilePath).IsRequired().UseCollation("NOCASE");
            e.HasIndex(x => x.FilePath).IsUnique();
            e.HasIndex(x => x.NormalizedKey);
            e.HasIndex(x => x.DateAdded);
            e.Ignore(x => x.DisplayArtist);
            e.Ignore(x => x.DisplayAlbum);
            e.Ignore(x => x.GroupingArtist);
        });

        b.Entity<Playlist>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.SourceType).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.SourceType, x.ExternalId });
            e.HasMany(x => x.Entries).WithOne(x => x.Playlist).HasForeignKey(x => x.PlaylistId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PlaylistEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PlaylistId, x.Position });
            e.HasOne(x => x.Track).WithMany().HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.SetNull);
        });

        // All timestamps are stored as UTC. SQLite has no time zone, so mark values as UTC when reading them back.
        var utc = new ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v,
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(
            v => v.HasValue && v.Value.Kind == DateTimeKind.Local ? v.Value.ToUniversalTime() : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTime)) property.SetValueConverter(utc);
                else if (property.ClrType == typeof(DateTime?)) property.SetValueConverter(utcNullable);
            }
        }
    }
}

/// <summary>Used by the EF Core tools to create migrations.</summary>
public sealed class SonicDbContextDesignTimeFactory : IDesignTimeDbContextFactory<SonicDbContext>
{
    public SonicDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SonicDbContext>().UseSqlite("Data Source=design.db").Options);
}
