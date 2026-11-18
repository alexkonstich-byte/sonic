using Sonic.Core.Models;
using Sonic.Core.Services;
using Xunit;

namespace Sonic.Core.Tests;

public class PlaylistDownloadTests
{
    private static readonly LocalTrackIndex Index = new(
    [
        new LocalAudioFile(@"C:\m\Rick Astley - Never Gonna Give You Up.mp3", "Rick Astley", "Never Gonna Give You Up", 213),
        new LocalAudioFile(@"C:\m\queen.mp3", "Queen", "Bohemian Rhapsody", 355),
        new LocalAudioFile(@"C:\m\daft.mp3", "Daft Punk feat. Pharrell Williams", "Get Lucky", 248),
        new LocalAudioFile(@"C:\m\untitled.mp3", null, null, 100),
    ]);

    [Fact]
    public void Matches_exact_artist_and_title() =>
        Assert.EndsWith("Never Gonna Give You Up.mp3", Index.Find("Rick Astley", "Never Gonna Give You Up", 212)!.Path);

    [Fact]
    public void Matches_spotify_version_suffix() =>
        Assert.Equal(@"C:\m\queen.mp3", Index.Find("Queen", "Bohemian Rhapsody - Remastered 2011", 354)?.Path);

    [Fact]
    public void Matches_first_of_several_artists() =>
        Assert.Equal(@"C:\m\daft.mp3", Index.Find("Daft Punk, Pharrell Williams, Nile Rodgers", "Get Lucky (feat. Pharrell Williams and Nile Rodgers)", 248)?.Path);

    [Fact]
    public void Rejects_same_name_with_very_different_length() =>
        Assert.Null(Index.Find("Queen", "Bohemian Rhapsody", 600));

    [Fact]
    public void Rejects_other_artist() => Assert.Null(Index.Find("Cake", "Never Gonna Give You Up", 213));

    [Theory]
    [InlineData("Song - Remastered 2011", "Song")]
    [InlineData("Song (Radio Edit)", "Song")]
    [InlineData("Song [Live] - 2004 Mix", "Song")]
    [InlineData("Plain", "Plain")]
    public void Strips_version_suffixes(string input, string expected) => Assert.Equal(expected, LocalTrackIndex.StripVersion(input));

    private static MediaInfo Result(string title, double duration) => new() { Url = "https://youtu.be/" + title.GetHashCode(), Title = title, DurationSeconds = duration };

    [Fact]
    public void Picks_closest_length_and_skips_unwanted_versions()
    {
        var entry = new PlaylistEntry { Artist = "Queen", Title = "Bohemian Rhapsody", DurationSeconds = 355 };
        var best = PlaylistDownloadService.PickBest(entry,
        [
            Result("Bohemian Rhapsody (Karaoke Version)", 355),
            Result("Queen - Bohemian Rhapsody (Official Video)", 367),
            Result("Queen – Bohemian Rhapsody (Live Aid 1985)", 357),
            Result("Bohemian Rhapsody", 356),
        ]);
        Assert.Equal("Bohemian Rhapsody", best?.Title);
    }

    [Fact]
    public void Returns_null_when_nothing_is_close()
    {
        var entry = new PlaylistEntry { Artist = "Queen", Title = "Bohemian Rhapsody", DurationSeconds = 355 };
        Assert.Null(PlaylistDownloadService.PickBest(entry, [Result("Bohemian Rhapsody 10 hours", 36000)]));
    }
}
