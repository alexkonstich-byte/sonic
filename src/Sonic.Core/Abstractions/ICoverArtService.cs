using Sonic.Core.Services;

namespace Sonic.Core.Abstractions;

/// <summary>Finds album covers in public music catalogs (iTunes, Deezer).</summary>
public interface ICoverArtService
{
    /// <summary>Candidates ordered by match score (best first). Never throws for network problems; returns an empty list.</summary>
    Task<IReadOnlyList<CoverCandidate>> SearchAsync(string? artist, string title, double? durationSeconds, CancellationToken cancellationToken = default);

    Task<byte[]> DownloadAsync(CoverCandidate candidate, CancellationToken cancellationToken = default);
}
