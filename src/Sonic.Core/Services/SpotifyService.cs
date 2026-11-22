using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;

namespace Sonic.Core.Services;

/// <summary>
/// Official Spotify Web API client using the Client Credentials flow. Only public catalog metadata is read
/// (playlist/album track lists, names, artwork). Sonic never requests or plays Spotify audio.
/// </summary>
public sealed partial class SpotifyService(
    ISettingsService settings,
    IHttpClientFactory httpFactory,
    ILogger<SpotifyService> logger) : ISpotifyService
{
    private const string TokenUrl = "https://accounts.spotify.com/api/token";
    private const string ApiBase = "https://api.spotify.com/v1/";
    private const int MaxItems = 10_000;

    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private DateTime _tokenExpiresUtc;
    private string? _tokenClientId;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(settings.Current.SpotifyClientId) && !string.IsNullOrWhiteSpace(settings.GetSpotifyClientSecret());

    public async Task VerifyCredentialsAsync(CancellationToken cancellationToken = default)
    {
        _token = null;
        await GetTokenAsync(cancellationToken).ConfigureAwait(false);
    }

    public static bool TryParseReference(string input, out string kind, out string id)
    {
        kind = id = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var m = SpotifyRefRegex().Match(input.Trim());
        if (!m.Success) return false;
        kind = m.Groups["kind"].Value.ToLowerInvariant();
        id = m.Groups["id"].Value;
        return true;
    }

    public async Task<SpotifyCollection> GetCollectionAsync(string urlOrUri, CancellationToken cancellationToken = default)
    {
        if (!TryParseReference(urlOrUri, out var kind, out var id))
            throw new SpotifyException("Enter a Spotify playlist or album link, for example https://open.spotify.com/playlist/…");

        return kind switch
        {
            "playlist" => await GetPlaylistAsync(id, cancellationToken).ConfigureAwait(false),
            "album" => await GetAlbumAsync(id, cancellationToken).ConfigureAwait(false),
            _ => throw new SpotifyException("Only playlist and album links can be imported."),
        };
    }

    public async Task<IReadOnlyList<SpotifyTrack>> SearchTracksAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        var url = $"search?type=track&limit={Math.Clamp(limit, 1, 10)}&q={Uri.EscapeDataString(query)}";
        using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        var list = new List<SpotifyTrack>();
        if (doc.RootElement.TryGetProperty("tracks", out var tracks) && tracks.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (ParseTrack(item, null) is { } t) list.Add(t);
            }
        }

        return list;
    }

    internal const string SignInForPlaylists =
        "Spotify now shares a playlist's songs only with signed-in users. Sign in with your Spotify account in Settings › Integrations › Spotify, then try again.";

    private async Task<SpotifyCollection> GetPlaylistAsync(string id, CancellationToken ct)
    {
        // Spotify only returns a playlist's songs to a signed-in user; app credentials get the name and cover only.
        var user = IsSignedIn;
        using var doc = await GetJsonAsync($"playlists/{id}", ct, user: user).ConfigureAwait(false);
        var root = doc.RootElement;
        var tracks = new List<SpotifyTrack>();
        var skipped = 0;

        // The paging object has been exposed as "tracks" and, in newer API versions, "items".
        JsonElement page = default;
        var hasPage = (root.TryGetProperty("items", out page) && page.ValueKind == JsonValueKind.Object)
                      || (root.TryGetProperty("tracks", out page) && page.ValueKind == JsonValueKind.Object);
        string? next;
        if (hasPage)
        {
            next = ReadPlaylistPage(page, tracks, ref skipped);
        }
        else if (!user)
        {
            throw new SpotifyException(SignInForPlaylists, 401);
        }
        else
        {
            // Songs left out of the playlist object: read them from the items endpoint.
            next = $"playlists/{id}/items?limit=50";
        }

        while (next is not null && tracks.Count < MaxItems)
        {
            JsonDocument nextDoc;
            try
            {
                nextDoc = await GetJsonAsync(next, ct, user: user).ConfigureAwait(false);
            }
            catch (SpotifyException ex) when (ex.StatusCode is 401 or 403 && tracks.Count == 0)
            {
                throw new SpotifyException(
                    "Spotify didn't share the songs of this playlist with Sonic. It may only do that for playlists you created or collaborate on: " +
                    "in Spotify, add the songs to a playlist of your own and paste that link.", ex.StatusCode);
            }

            using (nextDoc) next = ReadPlaylistPage(nextDoc.RootElement, tracks, ref skipped);
        }

        return new SpotifyCollection
        {
            Id = id,
            Name = Str(root, "name") ?? "Spotify playlist",
            Description = StripHtml(Str(root, "description")),
            Owner = root.TryGetProperty("owner", out var owner) ? Str(owner, "display_name") ?? Str(owner, "id") : null,
            ImageUrl = PickImage(root),
            ExternalUrl = root.TryGetProperty("external_urls", out var ext) ? Str(ext, "spotify") : $"https://open.spotify.com/playlist/{id}",
            Tracks = tracks,
            SkippedItems = skipped,
        };
    }

    private static string? ReadPlaylistPage(JsonElement page, List<SpotifyTrack> tracks, ref int skipped)
    {
        if (page.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                JsonElement trackEl = default;
                var has = (item.TryGetProperty("item", out trackEl) && trackEl.ValueKind == JsonValueKind.Object)
                          || (item.TryGetProperty("track", out trackEl) && trackEl.ValueKind == JsonValueKind.Object);
                var isLocal = item.TryGetProperty("is_local", out var local) && local.ValueKind == JsonValueKind.True;
                if (!has || isLocal || Str(trackEl, "type") is "episode")
                {
                    skipped++;
                    continue;
                }

                if (ParseTrack(trackEl, null) is { } t) tracks.Add(t);
                else skipped++;
            }
        }

        return Str(page, "next");
    }

    private async Task<SpotifyCollection> GetAlbumAsync(string id, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"albums/{id}", ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var albumName = Str(root, "name");
        var albumArtist = root.TryGetProperty("artists", out var aa) ? string.Join(", ", aa.EnumerateArray().Select(a => Str(a, "name")).Where(n => n is not null)) : null;
        var image = PickImage(root);
        var year = ParseYear(Str(root, "release_date"));
        var context = new AlbumContext(albumName, albumArtist, image, year);

        var tracks = new List<SpotifyTrack>();
        var skipped = 0;
        string? next = null;
        if (root.TryGetProperty("tracks", out var page))
        {
            next = ReadAlbumPage(page, tracks, context, ref skipped);
        }

        while (next is not null && tracks.Count < MaxItems)
        {
            using var nextDoc = await GetJsonAsync(next, ct).ConfigureAwait(false);
            next = ReadAlbumPage(nextDoc.RootElement, tracks, context, ref skipped);
        }

        return new SpotifyCollection
        {
            Id = id,
            Name = albumName ?? "Spotify album",
            Description = albumArtist,
            Owner = albumArtist,
            ImageUrl = image,
            ExternalUrl = root.TryGetProperty("external_urls", out var ext) ? Str(ext, "spotify") : $"https://open.spotify.com/album/{id}",
            IsAlbum = true,
            Tracks = tracks,
            SkippedItems = skipped,
        };
    }

    private static string? ReadAlbumPage(JsonElement page, List<SpotifyTrack> tracks, AlbumContext context, ref int skipped)
    {
        if (page.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (ParseTrack(item, context) is { } t) tracks.Add(t);
                else skipped++;
            }
        }

        return Str(page, "next");
    }

    private sealed record AlbumContext(string? Name, string? Artist, string? Image, uint? Year);

    private static SpotifyTrack? ParseTrack(JsonElement el, AlbumContext? album)
    {
        var id = Str(el, "id");
        var name = Str(el, "name");
        if (id is null || name is null) return null;

        var artists = el.TryGetProperty("artists", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(a => Str(a, "name")).Where(n => n is not null).Cast<string>().ToList()
            : [];

        string? albumName = album?.Name, albumArtist = album?.Artist, image = album?.Image;
        var year = album?.Year;
        if (el.TryGetProperty("album", out var alb) && alb.ValueKind == JsonValueKind.Object)
        {
            albumName = Str(alb, "name") ?? albumName;
            image = PickImage(alb) ?? image;
            year = ParseYear(Str(alb, "release_date")) ?? year;
            if (alb.TryGetProperty("artists", out var albArtists) && albArtists.ValueKind == JsonValueKind.Array)
                albumArtist = string.Join(", ", albArtists.EnumerateArray().Select(a => Str(a, "name")).Where(n => n is not null));
        }

        string? isrc = null;
        if (el.TryGetProperty("external_ids", out var ids)) isrc = Str(ids, "isrc");

        return new SpotifyTrack
        {
            Id = id,
            Name = name,
            Artists = artists,
            Album = albumName,
            AlbumArtist = string.IsNullOrWhiteSpace(albumArtist) ? null : albumArtist,
            TrackNumber = el.TryGetProperty("track_number", out var tn) && tn.TryGetUInt32(out var n) ? n : null,
            Year = year,
            DurationSeconds = el.TryGetProperty("duration_ms", out var dms) && dms.TryGetDouble(out var ms) ? ms / 1000.0 : 0,
            ImageUrl = image,
            Isrc = isrc,
        };
    }

    private async Task<JsonDocument> GetJsonAsync(string relativeOrAbsoluteUrl, CancellationToken ct, bool retried = false, bool user = false)
    {
        var token = user ? await GetUserTokenAsync(ct).ConfigureAwait(false) : await GetTokenAsync(ct).ConfigureAwait(false);
        var http = httpFactory.CreateClient(DependencyService.HttpClientName);
        var url = relativeOrAbsoluteUrl.StartsWith("https://", StringComparison.Ordinal) ? relativeOrAbsoluteUrl : ApiBase + relativeOrAbsoluteUrl;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Spotify request failed for {Url}", url);
            throw new SpotifyException("Could not reach Spotify. Check your internet connection.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized && !retried)
            {
                if (user) _userToken = null;
                else _token = null;
                return await GetJsonAsync(relativeOrAbsoluteUrl, ct, retried: true, user: user).ConfigureAwait(false);
            }

            if (response.StatusCode == (HttpStatusCode)429)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                if (!retried && wait < TimeSpan.FromSeconds(30))
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    return await GetJsonAsync(relativeOrAbsoluteUrl, ct, retried: true, user: user).ConfigureAwait(false);
                }

                throw new SpotifyException("Spotify is rate limiting requests. Try again in a minute.", 429);
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                logger.LogWarning("Spotify {Status} for {Url}: {Body}", (int)response.StatusCode, url, body);
                if (body.Contains("unavailable in this country", StringComparison.OrdinalIgnoreCase))
                    throw new SpotifyException(
                        "Spotify refuses requests from your country. Turn on a VPN, or set a proxy in Settings › Network, and try again. (zapret doesn't help here.)",
                        (int)response.StatusCode);
                if (response.StatusCode == HttpStatusCode.Unauthorized && !user)
                    throw new SpotifyException(SignInForPlaylists, 401);
                throw response.StatusCode switch
                {
                    HttpStatusCode.NotFound => new SpotifyException(
                        "Spotify couldn't find this item. It may be private, deleted, or a Spotify-curated playlist that isn't available to third-party apps.", 404),
                    HttpStatusCode.Forbidden => new SpotifyException(
                        "Spotify denied access. Your Spotify app may not have access to this endpoint (check its Development Mode settings in the Spotify Developer Dashboard).", 403),
                    _ => new SpotifyException($"Spotify returned an error ({(int)response.StatusCode}).", (int)response.StatusCode),
                };
            }

            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        var clientId = settings.Current.SpotifyClientId;
        var secret = settings.GetSpotifyClientSecret();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret))
            throw new SpotifyException("Spotify isn't configured. Add your Client ID and Client Secret in Settings › Integrations.");

        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && _tokenClientId == clientId && DateTime.UtcNow < _tokenExpiresUtc) return _token;

            var http = httpFactory.CreateClient(DependencyService.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("grant_type", "client_credentials")]),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}")));

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                throw new SpotifyException("Spotify rejected the Client ID or Client Secret.", (int)response.StatusCode);
            if (!response.IsSuccessStatusCode)
                throw new SpotifyException($"Spotify sign-in failed ({(int)response.StatusCode}).", (int)response.StatusCode);

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            _token = Str(doc.RootElement, "access_token") ?? throw new SpotifyException("Spotify returned an invalid token response.");
            var expires = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
            _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(expires - 60);
            _tokenClientId = clientId;
            return _token;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Spotify token request failed");
            throw new SpotifyException("Could not reach Spotify. Check your internet connection.");
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private static string? PickImage(JsonElement el)
    {
        if (!el.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return null;
        string? best = null;
        var bestWidth = -1;
        foreach (var img in images.EnumerateArray())
        {
            var url = Str(img, "url");
            var width = img.TryGetProperty("width", out var w) && w.TryGetInt32(out var wi) ? wi : 0;
            if (url is not null && width > bestWidth) { best = url; bestWidth = width; }
        }

        return best;
    }

    private static uint? ParseYear(string? date) =>
        date is { Length: >= 4 } && uint.TryParse(date[..4], out var y) ? y : null;

    private static string? StripHtml(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : WebUtility.HtmlDecode(HtmlTagRegex().Replace(s, string.Empty)).Trim();

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"(?:open\.spotify\.com/(?:intl-[a-z]{2}(?:-[a-z]{2})?/)?(?:embed/)?|spotify:)(?<kind>playlist|album|track)[/:](?<id>[A-Za-z0-9]{10,32})", RegexOptions.IgnoreCase)]
    private static partial Regex SpotifyRefRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
}
