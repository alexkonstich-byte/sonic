using Sonic.Core.Models;

namespace Sonic.Core.Abstractions;

/// <summary>
/// Read-only access to the official Spotify Web API (client-credentials flow) for playlist import
/// and metadata lookup. Sonic never accesses Spotify audio streams.
/// </summary>
public interface ISpotifyService
{
    bool IsConfigured { get; }

    Task VerifyCredentialsAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads a public playlist or album from a Spotify URL, URI or id.</summary>
    Task<SpotifyCollection> GetCollectionAsync(string urlOrUri, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SpotifyTrack>> SearchTracksAsync(string query, int limit, CancellationToken cancellationToken = default);
}

public sealed class SpotifyException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
