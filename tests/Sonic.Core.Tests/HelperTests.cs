using Sonic.Core.Helpers;
using Sonic.Core.Models;
using Sonic.Core.Services;

namespace Sonic.Core.Tests;

public class SourceDetectorTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", SourcePlatform.YouTube)]
    [InlineData("youtu.be/dQw4w9WgXcQ", SourcePlatform.YouTube)]
    [InlineData("https://music.youtube.com/watch?v=abc", SourcePlatform.YouTubeMusic)]
    [InlineData("https://m.youtube.com/watch?v=abc", SourcePlatform.YouTube)]
    [InlineData("https://vk.com/video-1_2", SourcePlatform.VK)]
    [InlineData("https://vkvideo.ru/video-1_2", SourcePlatform.VK)]
    [InlineData("https://rutube.ru/video/abc/", SourcePlatform.Rutube)]
    [InlineData("https://artist.bandcamp.com/track/x", SourcePlatform.Bandcamp)]
    [InlineData("https://soundcloud.com/a/b", SourcePlatform.SoundCloud)]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", SourcePlatform.Spotify)]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", SourcePlatform.Spotify)]
    [InlineData("https://example.org/video", SourcePlatform.Other)]
    [InlineData("not a url", SourcePlatform.Unknown)]
    [InlineData("", SourcePlatform.Unknown)]
    [InlineData("ftp://example.org/x", SourcePlatform.Unknown)]
    public void Detects_platform(string url, SourcePlatform expected) =>
        Assert.Equal(expected, SourceDetector.Detect(url));

    [Theory]
    [InlineData("https://music.apple.com/us/album/x/1", true)]
    [InlineData("https://www.deezer.com/track/1", true)]
    [InlineData("https://www.youtube.com/watch?v=1", false)]
    public void Flags_protected_streaming_services(string url, bool expected) =>
        Assert.Equal(expected, SourceDetector.IsProtectedStreamingService(url));
}

public class TitleParserTests
{
    [Theory]
    [InlineData("Daft Punk - Get Lucky (Official Video)", "Daft Punk", "Get Lucky")]
    [InlineData("Adele – Hello [Official Music Video]", "Adele", "Hello")]
    [InlineData("Queen - Bohemian Rhapsody (Remastered 2011) | Lyrics", "Queen", "Bohemian Rhapsody")]
    [InlineData("Artist - Song (Live at Wembley)", "Artist", "Song (Live at Wembley)")]
    public void Splits_artist_and_title(string raw, string artist, string title)
    {
        Assert.True(TitleParser.TrySplitArtistTitle(raw, out var a, out var t));
        Assert.Equal(artist, a);
        Assert.Equal(title, t);
    }

    [Theory]
    [InlineData("Just a title")]
    [InlineData("Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film")]
    [InlineData("Some Movie - Official Trailer")]
    [InlineData("My Game - Gameplay Walkthrough")]
    public void Does_not_split_non_music_titles(string raw) =>
        Assert.False(TitleParser.TrySplitArtistTitle(raw, out _, out _));

    [Fact]
    public void Removes_topic_suffix() =>
        Assert.Equal("Radiohead", TitleParser.CleanUploader("Radiohead - Topic"));

    [Fact]
    public void Normalized_keys_ignore_case_punctuation_accents_and_features()
    {
        var a = TitleParser.NormalizeKey("Beyoncé", "Halo (Official Video)");
        var b = TitleParser.NormalizeKey("beyonce", "HALO");
        var c = TitleParser.NormalizeKey("Beyonce, Jay-Z", "Halo feat. Someone");
        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }
}

public class FileNameHelperTests
{
    [Theory]
    [InlineData("AC/DC", "AC-DC")]
    [InlineData("What?*", "What")]
    [InlineData("Song: Part 1", "Song - Part 1")]
    [InlineData("CON", "_CON")]
    [InlineData("trailing.  ", "trailing")]
    [InlineData("", "Unknown")]
    public void Sanitizes_segments(string input, string expected) =>
        Assert.Equal(expected, FileNameHelper.SanitizeSegment(input));

    [Theory]
    [InlineData("{artist} - {title}", true)]
    [InlineData("{artist}/{album}/{track} - {title}", true)]
    [InlineData("{artist}", false)]
    [InlineData("{bogus} {title}", false)]
    [InlineData("../{title}", false)]
    [InlineData("C:\\{title}", false)]
    [InlineData("", false)]
    public void Validates_templates(string template, bool valid) =>
        Assert.Equal(valid, FileNameHelper.ValidateTemplate(template) is null);

    [Fact]
    public void Expands_template_with_folders()
    {
        var meta = new TrackMetadata { Title = "One", Artist = "Metallica", Album = "...And Justice for All", TrackNumber = 4 };
        var path = FileNameHelper.ExpandTemplate("{artist}/{album}/{track} - {title}", meta, null, "id1");
        Assert.Equal(Path.Combine("Metallica", "...And Justice for All", "04 - One"), path);
    }

    [Fact]
    public void Unique_path_never_overwrites()
    {
        var dir = Directory.CreateTempSubdirectory("sonic-test").FullName;
        try
        {
            var p = Path.Combine(dir, "a.mp3");
            Assert.Equal(p, FileNameHelper.GetUniquePath(p));
            File.WriteAllText(p, "x");
            Assert.Equal(Path.Combine(dir, "a (2).mp3"), FileNameHelper.GetUniquePath(p));
            File.WriteAllText(Path.Combine(dir, "a (2).mp3"), "x");
            Assert.Equal(Path.Combine(dir, "a (3).mp3"), FileNameHelper.GetUniquePath(p));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class YtDlpOutputParserTests
{
    [Fact]
    public void Parses_progress_line()
    {
        Assert.True(YtDlpOutputParser.TryParseProgress("[sonic-progress]downloading|1048576|4194304|NA|524288.5|6", out var p));
        Assert.Equal("downloading", p.Status);
        Assert.Equal(1048576, p.DownloadedBytes);
        Assert.Equal(4194304, p.TotalBytes);
        Assert.Equal(524288.5, p.SpeedBytesPerSecond);
        Assert.Equal(6, p.EtaSeconds);
    }

    [Fact]
    public void Uses_estimate_when_total_missing()
    {
        Assert.True(YtDlpOutputParser.TryParseProgress("[sonic-progress]downloading|10|NA|100.0|NA|NA", out var p));
        Assert.Equal(100, p.TotalBytes);
        Assert.Null(p.SpeedBytesPerSecond);
        Assert.Null(p.EtaSeconds);
    }

    [Fact]
    public void Ignores_other_lines() => Assert.False(YtDlpOutputParser.TryParseProgress("[download] 5%", out _));

    [Fact]
    public void Parses_file_line()
    {
        Assert.True(YtDlpOutputParser.TryParseFile(@"[sonic-file]C:\temp\media.webm", out var path));
        Assert.Equal(@"C:\temp\media.webm", path);
    }

    [Theory]
    [InlineData("ERROR: [youtube] abc: Video unavailable", false)]
    [InlineData("ERROR: [youtube] abc: Private video. Sign in if you've been granted access", false)]
    [InlineData("ERROR: Unable to download webpage: <urlopen error [Errno 11001] getaddrinfo failed>", true)]
    [InlineData("ERROR: [generic] Unsupported URL: https://x.y", false)]
    public void Describes_errors(string stderr, bool retryable)
    {
        var (message, isRetryable) = YtDlpOutputParser.Describe(stderr);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.DoesNotContain("ERROR:", message);
        Assert.Equal(retryable, isRetryable);
    }
}

public class VersionHelperTests
{
    [Theory]
    [InlineData("2026.10.26", "2026.11.01", -1)]
    [InlineData("2026.10.26", "2026.10.26", 0)]
    [InlineData("7.1.1", "7.1", 1)]
    [InlineData("v22.11.0", "22.11.0", 0)]
    public void Compares(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(VersionHelper.Compare(a, b)));
}

public class ConversionPlannerTests
{
    [Theory]
    [InlineData(AudioFormat.M4a, "aac", true, ".m4a")]
    [InlineData(AudioFormat.Opus, "opus", true, ".opus")]
    [InlineData(AudioFormat.Mp3, "opus", false, ".mp3")]
    [InlineData(AudioFormat.Mp3, "mp3", true, ".mp3")]
    [InlineData(AudioFormat.Flac, "opus", false, ".flac")]
    [InlineData(AudioFormat.Original, "opus", true, ".opus")]
    [InlineData(AudioFormat.Original, "aac", true, ".m4a")]
    [InlineData(AudioFormat.Ogg, "vorbis", true, ".ogg")]
    [InlineData(AudioFormat.Wav, "aac", false, ".wav")]
    public void Prefers_stream_copy(AudioFormat format, string codec, bool copy, string ext)
    {
        var plan = AudioConversionPlanner.Plan(format, AudioQuality.Best, codec, null);
        Assert.Equal(copy, plan.IsCopy);
        Assert.Equal(ext, plan.Extension);
    }

    [Fact]
    public void Mp3_quality_maps_to_bitrate()
    {
        var plan = AudioConversionPlanner.Plan(AudioFormat.Mp3, AudioQuality.Standard, "opus", null);
        Assert.Contains("192k", plan.CodecArguments);
        Assert.Contains("libmp3lame", plan.CodecArguments);
    }
}

public class DownloadPipelineTests
{
    [Fact]
    public void Metadata_prefers_hints_then_source_fields()
    {
        var item = new DownloadItem { Hints = new MetadataHints { Album = "Hint Album" } };
        var info = new MediaInfo { Title = "Some Artist - Some Song (Official Video)", Uploader = "Label VEVO", ReleaseYear = 2020 };
        var meta = DownloadPipeline.BuildMetadata(item, info, cleanTitles: true);
        Assert.Equal("Some Song", meta.Title);
        Assert.Equal("Some Artist", meta.Artist);
        Assert.Equal("Hint Album", meta.Album);
        Assert.Equal(2020u, meta.Year);
    }

    [Fact]
    public void Metadata_uses_track_and_artist_fields_when_present()
    {
        var info = new MediaInfo { Title = "whatever", Track = "Real Title", Artist = "Real Artist", Album = "LP" };
        var meta = DownloadPipeline.BuildMetadata(new DownloadItem(), info, cleanTitles: true);
        Assert.Equal("Real Title", meta.Title);
        Assert.Equal("Real Artist", meta.Artist);
        Assert.Equal("LP", meta.Album);
    }

    [Fact]
    public void Metadata_falls_back_to_uploader()
    {
        var info = new MediaInfo { Title = "Untitled jam", Uploader = "Band - Topic" };
        var meta = DownloadPipeline.BuildMetadata(new DownloadItem(), info, cleanTitles: true);
        Assert.Equal("Untitled jam", meta.Title);
        Assert.Equal("Band", meta.Artist);
    }

    [Theory]
    [InlineData(AudioFormat.M4a, null, "bestaudio[ext=m4a]/bestaudio/best")]
    [InlineData(AudioFormat.Flac, null, "bestaudio/best")]
    [InlineData(AudioFormat.Mp3, "251", "251/bestaudio[acodec=mp3]/bestaudio/best")]
    public void Builds_format_selector(AudioFormat format, string? formatId, string expected) =>
        Assert.Equal(expected, DownloadPipeline.BuildFormatSelector(new DownloadItem { Format = format, FormatId = formatId }));
}

public class SpotifyReferenceTests
{
    [Theory]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=abc", "playlist", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://open.spotify.com/intl-de/album/4aawyAB9vmqN3uQ7FjRGTy", "album", "4aawyAB9vmqN3uQ7FjRGTy")]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", "playlist", "37i9dQZF1DXcBWIGoYBM5M")]
    public void Parses_references(string input, string kind, string id)
    {
        Assert.True(SpotifyService.TryParseReference(input, out var k, out var i));
        Assert.Equal(kind, k);
        Assert.Equal(id, i);
    }

    [Fact]
    public void Rejects_other_urls() => Assert.False(SpotifyService.TryParseReference("https://youtube.com/watch?v=1", out _, out _));
}
