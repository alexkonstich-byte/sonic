using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;

namespace Sonic.Core.Services;

public sealed class FfmpegService(
    IDependencyService dependencies,
    IProcessRunner runner,
    ILogger<FfmpegService> logger) : IFfmpegService
{
    public async Task<AudioProbe> ProbeAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        var ffprobe = dependencies.FfprobePath;
        if (ffprobe is null) return new AudioProbe(null, null, null, null, null, null);

        var result = await runner.RunAsync(new ProcessRequest
        {
            FileName = ffprobe,
            Arguments =
            [
                "-v", "error",
                "-select_streams", "a:0",
                "-show_entries", "stream=codec_name,bit_rate,sample_rate,channels:format=format_name,duration,bit_rate",
                "-of", "json",
                inputPath,
            ],
            CaptureStandardOutput = true,
            Timeout = TimeSpan.FromMinutes(1),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            logger.LogWarning("ffprobe failed for {Path}: {Error}", inputPath, result.StandardErrorTail);
            return new AudioProbe(null, null, null, null, null, null);
        }

        return ParseProbe(result.StandardOutput);
    }

    internal static AudioProbe ParseProbe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? codec = null;
        int? bitrate = null, sampleRate = null, channels = null;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array && streams.GetArrayLength() > 0)
        {
            var s = streams[0];
            codec = GetString(s, "codec_name");
            bitrate = ParseInt(GetString(s, "bit_rate")) / 1000;
            sampleRate = ParseInt(GetString(s, "sample_rate"));
            channels = s.TryGetProperty("channels", out var ch) && ch.TryGetInt32(out var c) ? c : null;
        }

        string? container = null;
        double? duration = null;
        if (root.TryGetProperty("format", out var format))
        {
            container = GetString(format, "format_name");
            duration = double.TryParse(GetString(format, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            bitrate ??= ParseInt(GetString(format, "bit_rate")) / 1000;
        }

        return new AudioProbe(codec, container, bitrate, sampleRate, channels, duration);
    }

    public async Task<string> ConvertAudioAsync(
        string inputPath,
        string outputPathWithoutExtension,
        AudioFormat format,
        AudioQuality quality,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = dependencies.RequireFfmpeg();
        var probe = await ProbeAsync(inputPath, cancellationToken).ConfigureAwait(false);
        var plan = AudioConversionPlanner.Plan(format, quality, probe.Codec, Path.GetExtension(inputPath));
        var output = outputPathWithoutExtension + plan.Extension;
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(inputPath), StringComparison.OrdinalIgnoreCase))
            output = outputPathWithoutExtension + ".out" + plan.Extension;

        logger.LogInformation("Converting {Input} ({Codec}) → {Ext} [{Mode}]", Path.GetFileName(inputPath), probe.Codec ?? "unknown",
            plan.Extension, plan.IsCopy ? "stream copy" : "encode");

        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-i", inputPath,
            "-map", "0:a:0",
            "-vn", "-sn", "-dn",
            "-map_metadata", "0",
        };
        args.AddRange(plan.CodecArguments);
        args.AddRange(["-progress", "pipe:1", "-nostats", output]);

        var duration = probe.DurationSeconds;
        var result = await runner.RunAsync(new ProcessRequest { FileName = ffmpeg, Arguments = args },
            onOutputLine: line =>
            {
                if (duration is not > 0 || progress is null) return;
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) || line.StartsWith("out_time_ms=", StringComparison.Ordinal))
                {
                    var value = line[(line.IndexOf('=') + 1)..];
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                        progress.Report(Math.Clamp(us / 1_000_000.0 / duration.Value * 100.0, 0, 100));
                }
                else if (line == "progress=end")
                {
                    progress.Report(100);
                }
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0 || !File.Exists(output))
        {
            TryDelete(output);
            throw new FfmpegException("FFmpeg could not convert the audio.", result.StandardErrorTail);
        }

        return output;
    }

    public async Task<string> PrepareArtworkAsync(string imagePath, string outputPath, bool cropToSquare, CancellationToken cancellationToken = default)
    {
        var ffmpeg = dependencies.RequireFfmpeg();
        var filter = cropToSquare
            ? "crop='min(iw,ih)':'min(iw,ih)',scale='min(iw,1200)':-2"
            : "scale='min(iw,1600)':-2";
        var result = await runner.RunAsync(new ProcessRequest
        {
            FileName = ffmpeg,
            Arguments = ["-hide_banner", "-nostdin", "-y", "-i", imagePath, "-vf", filter, "-frames:v", "1", "-q:v", "2", outputPath],
            Timeout = TimeSpan.FromMinutes(1),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0 || !File.Exists(outputPath))
            throw new FfmpegException("Could not process the artwork image.", result.StandardErrorTail);
        return outputPath;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? ParseInt(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }
}

public sealed record ConversionPlan(string Extension, IReadOnlyList<string> CodecArguments, bool IsCopy);

/// <summary>Chooses FFmpeg arguments, preferring a lossless stream copy whenever the source codec already fits the target.</summary>
public static class AudioConversionPlanner
{
    public static ConversionPlan Plan(AudioFormat format, AudioQuality quality, string? sourceCodec, string? sourceExtension)
    {
        var codec = sourceCodec?.ToLowerInvariant() ?? GuessCodec(sourceExtension);

        switch (format)
        {
            case AudioFormat.Original:
                return codec switch
                {
                    "opus" => Copy(".opus"),
                    "aac" or "alac" => Copy(".m4a", "-movflags", "+faststart"),
                    "mp3" => Copy(".mp3", "-id3v2_version", "3"),
                    "vorbis" => Copy(".ogg"),
                    "flac" => Copy(".flac"),
                    _ when codec?.StartsWith("pcm_", StringComparison.Ordinal) == true => Copy(".wav"),
                    // Unknown codec: fall back to a high quality, widely supported encode.
                    _ => Plan(AudioFormat.M4a, AudioQuality.Best, "unknown", null),
                };

            case AudioFormat.Mp3:
                if (codec == "mp3") return Copy(".mp3", "-id3v2_version", "3");
                return Encode(".mp3", ["-c:a", "libmp3lame", .. quality switch
                {
                    AudioQuality.Best => (string[])["-q:a", "0"],
                    AudioQuality.High => ["-b:a", "320k"],
                    AudioQuality.Standard => ["-b:a", "192k"],
                    _ => ["-b:a", "128k"],
                }, "-id3v2_version", "3"]);

            case AudioFormat.M4a:
                if (codec is "aac" or "alac") return Copy(".m4a", "-movflags", "+faststart");
                return Encode(".m4a", ["-c:a", "aac", "-b:a", quality switch
                {
                    AudioQuality.Best => "320k",
                    AudioQuality.High => "256k",
                    AudioQuality.Standard => "192k",
                    _ => "128k",
                }, "-movflags", "+faststart"]);

            case AudioFormat.Opus:
                if (codec == "opus") return Copy(".opus");
                return Encode(".opus", ["-c:a", "libopus", "-b:a", quality switch
                {
                    AudioQuality.Best => "192k",
                    AudioQuality.High => "160k",
                    AudioQuality.Standard => "128k",
                    _ => "96k",
                }]);

            case AudioFormat.Ogg:
                if (codec is "vorbis" or "opus") return Copy(".ogg");
                return Encode(".ogg", ["-c:a", "libvorbis", "-q:a", quality switch
                {
                    AudioQuality.Best => "8",
                    AudioQuality.High => "6",
                    AudioQuality.Standard => "5",
                    _ => "3",
                }]);

            case AudioFormat.Flac:
                if (codec == "flac") return Copy(".flac");
                return Encode(".flac", ["-c:a", "flac", "-compression_level", "8"]);

            case AudioFormat.Wav:
                if (codec == "pcm_s16le") return Copy(".wav");
                return Encode(".wav", ["-c:a", "pcm_s16le"]);

            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }
    }

    public static bool IsLossless(AudioFormat format) => format is AudioFormat.Flac or AudioFormat.Wav;

    private static string? GuessCodec(string? extension) => extension?.ToLowerInvariant() switch
    {
        ".mp3" => "mp3",
        ".m4a" or ".aac" => "aac",
        ".opus" => "opus",
        ".ogg" => "vorbis",
        ".flac" => "flac",
        ".wav" => "pcm_s16le",
        _ => null, // .webm / .mp4 may contain several codecs; without ffprobe we must re-encode.
    };

    private static ConversionPlan Copy(string ext, params string[] extra) => new(ext, ["-c:a", "copy", .. extra], true);

    private static ConversionPlan Encode(string ext, string[] args) => new(ext, args, false);
}
