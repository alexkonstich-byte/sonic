using System.Text.Json;
using Sonic.Core.Helpers;
using Sonic.Core.Models;
using Sonic.Core.Services;

namespace Sonic.Core.Tests;

public class VideoOptionTests
{
    [Fact]
    public void Compatible_mp4_prefers_h264_and_aac_within_height()
    {
        var s = DownloadPipeline.BuildVideoFormatSelector(new VideoOptions { Container = VideoContainer.Mp4, MaxHeight = 720 });
        Assert.StartsWith("bv*[vcodec^=avc1][height<=720]+ba[ext=m4a]/b[ext=mp4][height<=720]/", s);
        Assert.EndsWith("bv*[height<=720]+ba/b[height<=720]/bv*+ba/b", s);
    }

    [Fact]
    public void Best_quality_has_no_codec_filter()
    {
        var s = DownloadPipeline.BuildVideoFormatSelector(new VideoOptions { Container = VideoContainer.Mkv, Codec = VideoCodecPreference.BestQuality });
        Assert.Equal("bv*+ba/b/bv*+ba/b", s);
    }

    [Fact]
    public void Mp4_arguments_merge_remux_and_embed()
    {
        var args = DownloadPipeline.BuildVideoArguments(new VideoOptions { Container = VideoContainer.Mp4 });
        Assert.Equal(["--merge-output-format", "mp4", "--remux-video", "mp4", "--embed-metadata", "--embed-chapters", "--embed-thumbnail"], args);
    }

    [Fact]
    public void Webm_skips_thumbnail_and_forces_vtt_for_embedded_subtitles()
    {
        var args = DownloadPipeline.BuildVideoArguments(new VideoOptions
        {
            Container = VideoContainer.Webm, SubtitleMode = SubtitleMode.Embed, SubtitleFormat = SubtitleFormat.Srt, SubtitleLanguages = "en, ru",
        });
        Assert.DoesNotContain("--embed-thumbnail", args);
        Assert.DoesNotContain("--remux-video", args);
        Assert.Contains("webm/mkv", args);
        Assert.Contains("--embed-subs", args);
        var i = args.IndexOf("--convert-subs");
        Assert.Equal("vtt", args[i + 1]);
        Assert.Equal("en,ru", args[args.IndexOf("--sub-langs") + 1]);
    }

    [Fact]
    public void Separate_subtitles_with_auto_captions()
    {
        var args = DownloadPipeline.BuildVideoArguments(new VideoOptions
        {
            Container = VideoContainer.Mp4, SubtitleMode = SubtitleMode.Separate, SubtitleFormat = SubtitleFormat.Ass,
            SubtitleLanguages = "en-orig", IncludeAutoSubtitles = true, EmbedThumbnail = false, EmbedChapters = false, EmbedMetadata = false,
        });
        Assert.Equal(["--merge-output-format", "mp4", "--remux-video", "mp4", "--write-subs", "--write-auto-subs", "--sub-langs", "en-orig",
            "--sub-format", "ass/best", "--convert-subs", "ass"], args);
    }

    [Fact]
    public void No_subtitle_arguments_without_languages()
    {
        var args = DownloadPipeline.BuildVideoArguments(new VideoOptions { SubtitleMode = SubtitleMode.Embed, SubtitleLanguages = " " });
        Assert.DoesNotContain("--write-subs", args);
    }

    [Fact]
    public void Video_template_expands_date_and_resolution()
    {
        var path = FileNameHelper.ExpandVideoTemplate("{uploader}/{date} {title} [{resolution}]", "Clip: Part 1", "Chan", "abc", "20240131", 1080);
        Assert.Equal(Path.Combine("Chan", "2024-01-31 Clip - Part 1 [1080p]"), path);
    }

    [Theory]
    [InlineData("{title} [{id}]", true)]
    [InlineData("{uploader}/{year}/{title}", true)]
    [InlineData("{artist} - {title}", false)]
    public void Video_templates_validate_their_own_tokens(string template, bool valid) =>
        Assert.Equal(valid, FileNameHelper.ValidateTemplate(template, MediaKind.Video) is null);

    [Theory]
    [InlineData("avc1.4d401e", "none", "mp4", "video")]
    [InlineData("none", "mp4a.40.2", "m4a", "audio")]
    [InlineData("NA", "NA", "vtt", "subtitles")]
    [InlineData("avc1", "mp4a", "mp4", "video")]
    [InlineData("NA", "NA", "m4a", "audio")]
    [InlineData("NA", "NA", "mp4", "video")]
    public void Classifies_streams(string vcodec, string acodec, string ext, string expected) =>
        Assert.Equal(expected, YtDlpOutputParser.ClassifyStream(vcodec, acodec, ext));

    [Fact]
    public void Progress_line_carries_stream_kind()
    {
        Assert.True(YtDlpOutputParser.TryParseProgress("[sonic-progress]downloading|10|100|NA|5|2|none|opus|webm", out var p));
        Assert.Equal("audio", p.Stream);
    }

    [Theory]
    [InlineData("ru", "Russian")]
    [InlineData("en-orig", "English (original)")]
    [InlineData("pt-BR", "Portuguese (Brazil)")]
    [InlineData("xx-nonsense", "xx-nonsense")]
    public void Names_subtitle_languages(string code, string expected) =>
        Assert.Equal(expected, YtDlpService.LanguageName(code));

    [Fact]
    public void Parses_postprocess_lines()
    {
        Assert.True(YtDlpOutputParser.TryParsePostprocess("[sonic-pp]started|Merger", out var status, out var pp));
        Assert.Equal("started", status);
        Assert.Equal("Merging video and audio", YtDlpOutputParser.DescribePostprocessor(pp));
    }

    [Fact]
    public void Analysis_lists_heights_and_filters_auto_captions()
    {
        const string json = """
        {
          "id": "x", "title": "T", "language": "en", "upload_date": "20240101", "height": 1080,
          "formats": [
            { "format_id": "137", "vcodec": "avc1", "acodec": "none", "height": 1080 },
            { "format_id": "136", "vcodec": "avc1", "acodec": "none", "height": 720 },
            { "format_id": "sb0", "vcodec": "none", "acodec": "none", "height": 90, "format_note": "storyboard" },
            { "format_id": "140", "vcodec": "none", "acodec": "mp4a.40.2", "abr": 128 }
          ],
          "subtitles": { "ru": [ { "ext": "vtt", "name": "Russian" } ], "live_chat": [ {} ] },
          "automatic_captions": {
            "en-orig": [ { "name": "English (Original)" } ], "en": [ { "name": "English" } ], "de": [ { "name": "German" } ]
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var info = YtDlpService.ParseInfo(doc.RootElement, null);
        Assert.Equal([1080, 720], info.VideoHeights);
        Assert.True(info.HasVideo);
        Assert.Equal("20240101", info.UploadDate);
        Assert.Equal(1080, info.Height);
        Assert.Equal(["ru", "en-orig", "en"], info.Subtitles.Select(s => s.Code));
        Assert.False(info.Subtitles[0].IsAuto);
        Assert.True(info.Subtitles[1].IsAuto);
        Assert.Equal("Russian", info.Subtitles[0].Name);
    }
}
