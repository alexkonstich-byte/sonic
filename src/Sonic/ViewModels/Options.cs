using Sonic.Core.Models;
using Sonic.Helpers;

namespace Sonic.ViewModels;

/// <summary>A value with a display label, for ComboBox/selector item sources.</summary>
public sealed record Option<T>(T Value, string Label, string? Description = null)
{
    public override string ToString() => Label;
}

public static class Options
{
    public static IReadOnlyList<Option<AudioFormat>> Formats { get; } =
    [
        new(AudioFormat.Mp3, "MP3", "Plays everywhere"),
        new(AudioFormat.M4a, "M4A (AAC)", "Great quality, small files"),
        new(AudioFormat.Opus, "Opus", "Most efficient modern codec"),
        new(AudioFormat.Ogg, "OGG", "Open format"),
        new(AudioFormat.Flac, "FLAC", "Lossless container"),
        new(AudioFormat.Wav, "WAV", "Uncompressed"),
        new(AudioFormat.Original, "Original", "Keep the source audio without re-encoding"),
    ];

    public static IReadOnlyList<Option<AudioQuality>> Qualities { get; } =
    [
        new(AudioQuality.Best, "Best"),
        new(AudioQuality.High, "High"),
        new(AudioQuality.Standard, "Standard"),
        new(AudioQuality.Low, "Low"),
    ];

    public static IReadOnlyList<Option<AppTheme>> Themes { get; } =
    [
        new(AppTheme.System, "Use system setting"),
        new(AppTheme.Dark, "Dark"),
        new(AppTheme.Light, "Light"),
    ];

    public static IReadOnlyList<Option<StartupPage>> StartupPages { get; } =
    [
        new(StartupPage.Home, "Home"),
        new(StartupPage.Downloads, "Downloads"),
        new(StartupPage.Library, "Music Library"),
    ];

    public static IReadOnlyList<Option<DuplicateHandling>> DuplicateHandlings { get; } =
    [
        new(DuplicateHandling.KeepBoth, "Download anyway"),
        new(DuplicateHandling.Skip, "Skip songs already in the library"),
    ];

    public static IReadOnlyList<Option<VideoContainer>> Containers { get; } =
    [
        new(VideoContainer.Mp4, "MP4", "Plays everywhere"),
        new(VideoContainer.Mkv, "MKV", "Any codec, best for subtitles"),
        new(VideoContainer.Webm, "WebM", "Open web format"),
    ];

    public static IReadOnlyList<int> StandardHeights { get; } = [4320, 2160, 1440, 1080, 720, 480, 360, 240, 144];

    public static Option<int> HeightOption(int height) => height switch
    {
        <= 0 => new(0, "Best available"),
        4320 => new(4320, "4320p (8K)"),
        2160 => new(2160, "2160p (4K)"),
        1440 => new(1440, "1440p (2K)"),
        1080 => new(1080, "1080p (Full HD)"),
        720 => new(720, "720p (HD)"),
        _ => new(height, $"{height}p"),
    };

    public static IReadOnlyList<Option<int>> StandardVideoQualities { get; } =
        [HeightOption(0), .. StandardHeights.Select(HeightOption)];

    public static IReadOnlyList<Option<VideoCodecPreference>> VideoCodecs { get; } =
    [
        new(VideoCodecPreference.Compatible, "Most compatible (H.264)"),
        new(VideoCodecPreference.BestQuality, "Best quality (VP9/AV1)"),
    ];

    public static IReadOnlyList<Option<SubtitleMode>> SubtitleModes { get; } =
    [
        new(SubtitleMode.None, "No subtitles"),
        new(SubtitleMode.Embed, "Embed in video"),
        new(SubtitleMode.Separate, "Save as separate files"),
    ];

    public static IReadOnlyList<Option<SubtitleFormat>> SubtitleFormats { get; } =
    [
        new(SubtitleFormat.Srt, "SRT"),
        new(SubtitleFormat.Vtt, "WebVTT"),
        new(SubtitleFormat.Ass, "ASS"),
    ];

    /// <summary>Picks the largest option not above <paramref name="preferred"/> (0 = best).</summary>
    public static Option<int> PickHeight(IEnumerable<Option<int>> options, int preferred)
    {
        var list = options.ToList();
        if (preferred <= 0) return list.First(o => o.Value == 0);
        return list.Where(o => o.Value > 0 && o.Value <= preferred).OrderByDescending(o => o.Value).FirstOrDefault()
               ?? list.Where(o => o.Value > 0).OrderBy(o => o.Value).FirstOrDefault()
               ?? list[0];
    }

    public static Option<T> Find<T>(IReadOnlyList<Option<T>> options, T value) =>
        options.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, value)) ?? options[0];

    public static string QualityLabel(AudioQuality quality, AudioFormat format) => Format.QualityName(quality, format);
}
