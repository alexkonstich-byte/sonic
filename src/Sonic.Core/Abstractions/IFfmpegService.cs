using Sonic.Core.Models;

namespace Sonic.Core.Abstractions;

public sealed record AudioProbe(string? Codec, string? Container, int? BitrateKbps, int? SampleRate, int? Channels, double? DurationSeconds);

public interface IFfmpegService
{
    Task<AudioProbe> ProbeAsync(string inputPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts (or losslessly remuxes, when possible) the audio stream of <paramref name="inputPath"/>.
    /// Returns the path of the created file, whose extension is chosen from <paramref name="format"/>.
    /// </summary>
    Task<string> ConvertAudioAsync(
        string inputPath,
        string outputPathWithoutExtension,
        AudioFormat format,
        AudioQuality quality,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>Normalizes an image to JPEG (optionally center-cropped to a square) for embedding as cover art.</summary>
    Task<string> PrepareArtworkAsync(string imagePath, string outputPath, bool cropToSquare, CancellationToken cancellationToken = default);
}

public sealed class FfmpegException(string message, string? details) : Exception(message)
{
    public string? Details { get; } = details;
}
