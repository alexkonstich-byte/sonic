using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Data;
using Sonic.Core.Models;
using Sonic.Core.Services;
using Xunit.Abstractions;

namespace Sonic.Core.Tests;

/// <summary>
/// Offline tests against real FFmpeg/TagLib/SQLite: tagging and artwork in every output format, library scanning,
/// duplicate detection, metadata editing and playlists. Skipped when FFmpeg isn't installed.
/// </summary>
public sealed class LibraryIntegrationTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("sonic-lib").FullName;
    private ServiceProvider _sp = null!;
    private string _music = null!;
    private string? _ffmpeg;

    public async Task InitializeAsync()
    {
        _music = Path.Combine(_root, "music");
        Directory.CreateDirectory(_music);
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(new XunitLoggerProvider(output)).SetMinimumLevel(LogLevel.Warning));
        services.AddSonicCore("Sonic-Tests/1.0");
        services.AddSingleton<IAppPaths>(new AppPaths(Path.Combine(_root, "data")));
        _sp = services.BuildServiceProvider();
        var settings = _sp.GetRequiredService<ISettingsService>();
        await settings.LoadAsync();
        await settings.UpdateAsync(s =>
        {
            s.DownloadDirectory = Path.Combine(_root, "downloads");
            s.MusicFolders = [_music];
        });
        await _sp.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        var deps = _sp.GetRequiredService<IDependencyService>();
        await deps.RefreshAsync(checkForUpdates: false);
        _ffmpeg = deps.GetStatus(ToolKind.Ffmpeg).IsUsable ? deps.RequireFfmpeg() : null;
    }

    public async Task DisposeAsync()
    {
        _sp.GetRequiredService<ILibraryService>().Dispose();
        await _sp.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>Generates a short sine-wave file with FFmpeg.</summary>
    private async Task<string> MakeAudioAsync(string path, int seconds = 3, string? codecArgs = null)
    {
        var runner = _sp.GetRequiredService<IProcessRunner>();
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}" };
        if (codecArgs is not null) args.AddRange(codecArgs.Split(' '));
        args.Add(path);
        var result = await runner.RunAsync(new ProcessRequest { FileName = _ffmpeg!, Arguments = args });
        Assert.True(result.ExitCode == 0, result.StandardErrorTail);
        return path;
    }

    private async Task<byte[]> MakeJpegAsync()
    {
        var path = Path.Combine(_root, "cover.jpg");
        var runner = _sp.GetRequiredService<IProcessRunner>();
        var r = await runner.RunAsync(new ProcessRequest
        {
            FileName = _ffmpeg!,
            Arguments = ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "color=c=0x3366cc:s=320x180", "-frames:v", "1", path],
        });
        Assert.Equal(0, r.ExitCode);
        // Exercise the artwork pipeline used for downloads (center crop to square).
        var square = await _sp.GetRequiredService<IFfmpegService>().PrepareArtworkAsync(path, Path.Combine(_root, "square.jpg"), cropToSquare: true);
        return await File.ReadAllBytesAsync(square);
    }

    [SkippableTheory]
    [InlineData(".mp3", "-c:a libmp3lame -b:a 128k")]
    [InlineData(".flac", null)]
    [InlineData(".m4a", "-c:a aac -b:a 128k")]
    [InlineData(".opus", "-c:a libopus -b:a 64k")]
    [InlineData(".ogg", "-c:a libvorbis")]
    [InlineData(".wav", null)]
    public async Task Writes_and_reads_tags_with_artwork(string ext, string? codec)
    {
        Skip.If(_ffmpeg is null, "FFmpeg not installed");
        var file = await MakeAudioAsync(Path.Combine(_root, "tagtest" + ext), codecArgs: codec);
        var art = await MakeJpegAsync();
        var meta = _sp.GetRequiredService<IMetadataService>();
        meta.Write(file, new TrackMetadata
        {
            Title = "Тест Title", Artist = "Artist Ä", Album = "Album", AlbumArtist = "Album Artist",
            TrackNumber = 7, Year = 2024, Genre = "Electronic", Comment = "https://example.org",
            Artwork = art, ArtworkMimeType = "image/jpeg",
        }, overwriteAll: true);

        var read = meta.Read(file);
        Assert.Equal("Тест Title", read.Title);
        Assert.Equal("Artist Ä", read.Artist);
        Assert.Equal("Album", read.Album);
        Assert.Equal("Album Artist", read.AlbumArtist);
        Assert.Equal(7u, read.TrackNumber);
        Assert.Equal(2024u, read.Year);
        Assert.Equal("Electronic", read.Genre);
        Assert.NotNull(read.Artwork);
        Assert.Equal(art.Length, read.Artwork!.Length);
        Assert.InRange(read.DurationSeconds, 2.5, 3.5);

        // Partial writes keep existing values (used when tagging downloads).
        meta.Write(file, new TrackMetadata { Title = "New title" }, overwriteAll: false);
        var again = meta.Read(file);
        Assert.Equal("New title", again.Title);
        Assert.Equal("Album", again.Album);
        Assert.NotNull(again.Artwork);
    }

    [SkippableFact]
    public async Task Converts_with_stream_copy_when_codec_matches()
    {
        Skip.If(_ffmpeg is null, "FFmpeg not installed");
        var source = await MakeAudioAsync(Path.Combine(_root, "src.m4a"), codecArgs: "-c:a aac -b:a 128k");
        var ffmpeg = _sp.GetRequiredService<IFfmpegService>();
        var progress = new List<double>();
        var copied = await ffmpeg.ConvertAudioAsync(source, Path.Combine(_root, "copied"), AudioFormat.M4a, AudioQuality.Best, new Helpers.SyncProgress<double>(progress.Add));
        Assert.Equal(".m4a", Path.GetExtension(copied));
        Assert.Equal("aac", (await ffmpeg.ProbeAsync(copied)).Codec);

        var mp3 = await ffmpeg.ConvertAudioAsync(source, Path.Combine(_root, "encoded"), AudioFormat.Mp3, AudioQuality.Standard, null);
        Assert.Equal("mp3", (await ffmpeg.ProbeAsync(mp3)).Codec);
        Assert.NotEmpty(progress);
    }

    [SkippableFact]
    public async Task Scans_library_detects_duplicates_and_edits_metadata()
    {
        Skip.If(_ffmpeg is null, "FFmpeg not installed");
        var meta = _sp.GetRequiredService<IMetadataService>();
        var tagged = await MakeAudioAsync(Path.Combine(_music, "a.mp3"));
        meta.Write(tagged, new TrackMetadata { Title = "Halo", Artist = "Beyoncé", Album = "I Am", Artwork = await MakeJpegAsync() }, true);
        Directory.CreateDirectory(Path.Combine(_music, "Sub"));
        var dup = await MakeAudioAsync(Path.Combine(_music, "Sub", "b.flac"));
        meta.Write(dup, new TrackMetadata { Title = "Halo (Official Video)", Artist = "Beyonce" }, true);
        // Untagged file named "Artist - Title" gets its tags from the file name.
        await MakeAudioAsync(Path.Combine(_music, "Daft Punk - One More Time.wav"));

        var library = _sp.GetRequiredService<ILibraryService>();
        var result = await library.ScanAsync(null);
        Assert.Equal(3, result.Added);
        var tracks = await library.GetTracksAsync();
        Assert.Contains(tracks, t => t.Title == "One More Time" && t.Artist == "Daft Punk");
        Assert.Contains(tracks, t => t.Title == "Halo" && t.ArtworkPath is not null && File.Exists(t.ArtworkPath));

        var dups = await library.FindDuplicatesAsync();
        Assert.Single(dups);
        Assert.Equal(2, dups[0].Tracks.Count);

        // A rescan without changes does nothing.
        var second = await library.ScanAsync(null);
        Assert.Equal(0, second.Added + second.Updated + second.Removed);

        // Edit metadata through the library (writes the file and refreshes the row).
        var halo = tracks.First(t => t.Title == "Halo");
        await library.SetFavoriteAsync(halo.Id, true);
        var updated = await library.UpdateMetadataAsync(halo.Id, new TrackMetadata { Title = "Halo", Artist = "Beyoncé", Album = "I Am... Sasha Fierce", Year = 2008 }, removeArtwork: false);
        Assert.Equal("I Am... Sasha Fierce", updated.Album);
        Assert.True((await library.GetTrackAsync(halo.Id))!.IsFavorite);
        Assert.Equal(2008u, meta.Read(halo.FilePath).Year);

        // Removing a file from disk removes it from the library on the next scan.
        File.Delete(dup);
        var third = await library.ScanAsync(null);
        Assert.Equal(1, third.Removed);

        // Remove from library without deleting the file.
        await library.RemoveAsync([halo.Id], deleteFiles: false);
        Assert.True(File.Exists(halo.FilePath));
        Assert.DoesNotContain(await library.GetTracksAsync(), t => t.Id == halo.Id);
    }

    [SkippableFact]
    public async Task Playlists_support_crud_reorder_export_and_spotify_matching()
    {
        Skip.If(_ffmpeg is null, "FFmpeg not installed");
        var meta = _sp.GetRequiredService<IMetadataService>();
        var library = _sp.GetRequiredService<ILibraryService>();
        var playlists = _sp.GetRequiredService<IPlaylistService>();

        var f1 = await MakeAudioAsync(Path.Combine(_music, "1.mp3"), 4);
        meta.Write(f1, new TrackMetadata { Title = "Get Lucky", Artist = "Daft Punk" }, true);
        var f2 = await MakeAudioAsync(Path.Combine(_music, "2.mp3"), 3);
        meta.Write(f2, new TrackMetadata { Title = "Instant Crush", Artist = "Daft Punk" }, true);
        await library.ScanAsync(null);
        var tracks = await library.GetTracksAsync();

        var local = await playlists.CreateAsync("  Road trip ");
        Assert.Equal("Road trip", local.Name);
        await playlists.AddTracksAsync(local.Id, tracks.Select(t => t.Id).ToList());
        var loaded = (await playlists.GetPlaylistAsync(local.Id))!;
        Assert.Equal(2, loaded.Entries.Count);

        var reversed = loaded.Entries.Select(e => e.Id).Reverse().ToList();
        await playlists.ReorderAsync(local.Id, reversed);
        Assert.Equal(reversed, (await playlists.GetPlaylistAsync(local.Id))!.Entries.Select(e => e.Id));

        var m3u = Path.Combine(_root, "out.m3u8");
        await playlists.ExportM3uAsync(local.Id, m3u);
        var lines = await File.ReadAllLinesAsync(m3u);
        Assert.Equal("#EXTM3U", lines[0]);
        Assert.Contains(lines, l => l.EndsWith("1.mp3", StringComparison.OrdinalIgnoreCase));

        await playlists.RenameAsync(local.Id, "Renamed");
        await Assert.ThrowsAsync<ArgumentException>(() => playlists.RenameAsync(local.Id, "   "));

        // Spotify import: entries carry metadata; matching links them to local files by artist/title/duration.
        var collection = new SpotifyCollection
        {
            Id = "37i9dQZF1DX0000000000000",
            Name = "Imported",
            Tracks =
            [
                new SpotifyTrack { Id = "t1", Name = "Get Lucky (feat. Pharrell Williams)", Artists = ["Daft Punk", "Pharrell Williams"], DurationSeconds = 4.5, Album = "RAM" },
                new SpotifyTrack { Id = "t2", Name = "Contact", Artists = ["Daft Punk"], DurationSeconds = 381 },
            ],
        };
        var imported = await playlists.ImportSpotifyAsync(collection);
        Assert.Equal(PlaylistSource.Spotify, imported.SourceType);
        Assert.Equal(2, imported.Entries.Count);
        Assert.NotNull(imported.Entries.Single(e => e.ExternalId == "t1").TrackId);
        Assert.Null(imported.Entries.Single(e => e.ExternalId == "t2").TrackId);

        // Re-importing refreshes in place (no duplicate playlist) and keeps existing links.
        var again = await playlists.ImportSpotifyAsync(collection);
        Assert.Equal(imported.Id, again.Id);
        Assert.NotNull(again.Entries.Single(e => e.ExternalId == "t1").TrackId);
        Assert.Equal(2, (await playlists.GetPlaylistsAsync()).Count);

        // Deleting a library track keeps the playlist row but unlinks it.
        var luckyId = again.Entries.Single(e => e.ExternalId == "t1").TrackId!.Value;
        await library.RemoveAsync([luckyId], deleteFiles: false);
        Assert.Null((await playlists.GetPlaylistAsync(again.Id))!.Entries.Single(e => e.ExternalId == "t1").TrackId);

        await playlists.DeleteAsync(local.Id);
        Assert.Single(await playlists.GetPlaylistsAsync());
    }
}
