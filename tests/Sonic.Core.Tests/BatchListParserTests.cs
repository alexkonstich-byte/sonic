using Sonic.Core.Helpers;

namespace Sonic.Core.Tests;

public class BatchListParserTests
{
    [Fact]
    public void Parses_pasted_lines_with_links_queries_numbering_and_comments()
    {
        var items = BatchListParser.ParseLines("""
            # my list
            https://www.youtube.com/watch?v=abc
            1. Daft Punk - Get Lucky
            02) Radiohead – Creep

            Bohemian Rhapsody
            daft punk - get lucky
            """);
        Assert.Equal(4, items.Count); // duplicate "get lucky" removed
        Assert.True(items[0].IsLink);
        Assert.Equal("Daft Punk", items[1].Artist);
        Assert.Equal("Get Lucky", items[1].Title);
        Assert.Equal("Radiohead - Creep", items[2].Query);
        Assert.Equal("Bohemian Rhapsody", items[3].Query);
    }

    [Theory]
    [InlineData("A - One\rB - Two\rC - Three")]      // WinUI TextBox line breaks
    [InlineData("A - One\r\nB - Two\r\nC - Three")]  // Windows files
    [InlineData("A - One\nB - Two\nC - Three")]      // Unix files
    public void Accepts_any_line_break_style(string text) =>
        Assert.Equal(3, BatchListParser.ParseLines(text).Count);

    [Fact]
    public void Parses_exportify_spotify_csv()
    {
        const string csv = "\"Track URI\",\"Track Name\",\"Artist Name(s)\",\"Album Name\",\"Duration (ms)\"\n" +
                           "\"spotify:track:1\",\"Get Lucky (feat. Pharrell Williams)\",\"Daft Punk;Pharrell Williams\",\"Random Access Memories\",\"369626\"\n" +
                           "\"spotify:track:2\",\"Song, with comma\",\"Artist \"\"Quoted\"\"\",\"Album\",\"200000\"\n";
        var items = BatchListParser.ParseCsv(csv);
        Assert.Equal(2, items.Count);
        Assert.Equal("Daft Punk", items[0].Artist);
        Assert.Equal("Random Access Memories", items[0].Album);
        Assert.Equal(369.626, items[0].DurationSeconds!.Value, 3);
        Assert.Equal("Song, with comma", items[1].Title);
        Assert.Equal("Artist \"Quoted\"", items[1].Artist);
        Assert.False(items[0].IsLink);
    }

    [Fact]
    public void Parses_semicolon_csv_with_urls_and_mm_ss()
    {
        var items = BatchListParser.ParseCsv("title;artist;url;length\nOne;Metallica;https://youtu.be/x;7:27\nTwo;Band;;3:05\n");
        Assert.Equal("https://youtu.be/x", items[0].Url);
        Assert.Equal(447, items[0].DurationSeconds);
        Assert.False(items[1].IsLink);
        Assert.Equal(185, items[1].DurationSeconds);
    }

    [Fact]
    public void Parses_headerless_csv()
    {
        var items = BatchListParser.ParseCsv("Queen,Bohemian Rhapsody\nhttps://example.org/v\n");
        Assert.Equal("Queen - Bohemian Rhapsody", items[0].Query);
        Assert.True(items[1].IsLink);
    }

    [Fact]
    public void Parses_m3u_from_other_players()
    {
        var items = BatchListParser.ParseM3u("""
            #EXTM3U
            #EXTINF:215,Muse - Uprising
            C:\Music\Muse\uprising.mp3
            #EXTINF:-1,Stream
            https://example.org/stream
            D:\Other\Nirvana - Lithium.flac
            """);
        Assert.Equal(3, items.Count);
        Assert.Equal("Muse", items[0].Artist);
        Assert.Equal(215, items[0].DurationSeconds);
        Assert.True(items[1].IsLink);
        Assert.Equal("Nirvana - Lithium", items[2].Query);
    }
}
