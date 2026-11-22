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

    /// <summary>True after the user signed in to their own Spotify account (needed for Liked Songs).</summary>
    bool IsSignedIn { get; }

    string? SignedInUser { get; }

    /// <summary>Signs in through the browser (Authorization Code + PKCE, loopback redirect to Sonic).</summary>
    Task SignInAsync(Func<Uri, Task> openBrowser, CancellationToken cancellationToken = default);

    Task SignOutAsync();

    /// <summary>The signed-in user's Liked Songs (newest first).</summary>
    Task<SpotifyLikedSongs> GetLikedSongsAsync(int maxItems = 10_000, CancellationToken cancellationToken = default);
}

public sealed class SpotifyException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
