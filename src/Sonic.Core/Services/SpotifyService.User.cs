using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;

namespace Sonic.Core.Services;

/// <summary>
/// Signing in to the user's own Spotify account (Authorization Code with PKCE, loopback redirect) to read their
/// library, e.g. Liked Songs. Only metadata is read. The refresh token is stored DPAPI-encrypted in settings.
/// </summary>
public sealed partial class SpotifyService
{
    public const int RedirectPort = 43821;
    public const string RedirectUri = "http://127.0.0.1:43821/callback";
    private const string Scopes = "user-library-read playlist-read-private playlist-read-collaborative";

    /// <summary>How long one loopback connection may stay silent before it's dropped (internal for tests).</summary>
    internal static TimeSpan ConnectionReadTimeout { get; set; } = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _userTokenGate = new(1, 1);
    private string? _userToken;
    private DateTime _userTokenExpiresUtc;

    public bool IsSignedIn => !string.IsNullOrEmpty(settings.Current.SpotifyRefreshTokenProtected);

    public string? SignedInUser => IsSignedIn ? settings.Current.SpotifyUserName : null;

    public async Task SignInAsync(Func<Uri, Task> openBrowser, CancellationToken cancellationToken = default)
    {
        var clientId = settings.Current.SpotifyClientId;
        if (string.IsNullOrWhiteSpace(clientId))
            throw new SpotifyException("Add your Spotify Client ID in Settings › Integrations › Spotify first.");

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var authorize = "https://accounts.spotify.com/authorize?" + string.Join('&',
            $"client_id={Uri.EscapeDataString(clientId)}",
            "response_type=code",
            $"redirect_uri={Uri.EscapeDataString(RedirectUri)}",
            "code_challenge_method=S256",
            $"code_challenge={challenge}",
            $"scope={Uri.EscapeDataString(Scopes)}",
            $"state={state}");

        var listener = new TcpListener(IPAddress.Loopback, RedirectPort);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new SpotifyException($"Port {RedirectPort} is in use, so Spotify can't send the sign-in back to Sonic ({ex.SocketErrorCode}).");
        }

        try
        {
            await openBrowser(new Uri(authorize)).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var query = await WaitForCallbackAsync(listener, timeout.Token).ConfigureAwait(false);

            if (query.TryGetValue("error", out var error))
                throw new SpotifyException(error == "access_denied" ? "Sign-in was canceled in the browser." : $"Spotify sign-in failed: {error}.");
            if (!query.TryGetValue("state", out var returnedState) || returnedState != state || !query.TryGetValue("code", out var code))
                throw new SpotifyException("Spotify sent an unexpected sign-in response. Try again.");

            await RequestUserTokenAsync(clientId,
            [
                new("grant_type", "authorization_code"),
                new("code", code),
                new("redirect_uri", RedirectUri),
                new("client_id", clientId),
                new("code_verifier", verifier),
            ], cancellationToken).ConfigureAwait(false);

            using var me = await GetJsonAsync("me", cancellationToken, user: true).ConfigureAwait(false);
            var name = Str(me.RootElement, "display_name") ?? Str(me.RootElement, "id") ?? "Spotify user";
            await settings.UpdateAsync(s => s.SpotifyUserName = name).ConfigureAwait(false);
            logger.LogInformation("Signed in to Spotify");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SpotifyException("Sign-in timed out. Finish signing in within 5 minutes.");
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task SignOutAsync()
    {
        _userToken = null;
        await settings.UpdateAsync(s =>
        {
            s.SpotifyRefreshTokenProtected = null;
            s.SpotifyUserName = null;
        }).ConfigureAwait(false);
    }

    public async Task<SpotifyLikedSongs> GetLikedSongsAsync(int maxItems = MaxItems, CancellationToken cancellationToken = default)
    {
        var tracks = new List<SpotifyTrack>();
        string? next = "me/tracks?limit=50";
        var total = 0;
        while (next is not null && tracks.Count < maxItems)
        {
            using var doc = await GetJsonAsync(next, cancellationToken, user: true).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.TryGetProperty("total", out var t) && t.TryGetInt32(out var n)) total = n;
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("track", out var trackEl) || trackEl.ValueKind != JsonValueKind.Object) continue;
                    if (ParseTrack(trackEl, null) is not { } track) continue;
                    DateTime? added = Str(item, "added_at") is { } a && DateTime.TryParse(a, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
                    tracks.Add(track.WithAddedAt(added));
                }
            }

            next = Str(root, "next");
        }

        return new SpotifyLikedSongs(total, tracks);
    }

    private async Task<string> GetUserTokenAsync(CancellationToken ct)
    {
        var clientId = settings.Current.SpotifyClientId;
        var refresh = SecretProtector.Unprotect(settings.Current.SpotifyRefreshTokenProtected);
        if (string.IsNullOrWhiteSpace(clientId) || refresh is null)
            throw new SpotifyException("Sign in to Spotify in Settings › Integrations › Spotify.");

        await _userTokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_userToken is not null && DateTime.UtcNow < _userTokenExpiresUtc) return _userToken;
        }
        finally
        {
            _userTokenGate.Release();
        }

        await RequestUserTokenAsync(clientId, [new("grant_type", "refresh_token"), new("refresh_token", refresh), new("client_id", clientId)], ct).ConfigureAwait(false);
        return _userToken!;
    }

    private async Task RequestUserTokenAsync(string clientId, KeyValuePair<string, string>[] form, CancellationToken ct)
    {
        await _userTokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var http = httpFactory.CreateClient(DependencyService.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(form) };
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Spotify user token request failed: {Status} {Body}", (int)response.StatusCode, body);
                if (body.Contains("invalid_grant", StringComparison.Ordinal) && form.Any(f => f is { Key: "grant_type", Value: "refresh_token" }))
                {
                    // Revoked or expired: the user has to sign in again.
                    await settings.UpdateAsync(s => s.SpotifyRefreshTokenProtected = null).ConfigureAwait(false);
                    throw new SpotifyException("Your Spotify sign-in expired. Sign in again in Settings › Integrations › Spotify.", 401);
                }

                throw new SpotifyException(body.Contains("redirect_uri", StringComparison.OrdinalIgnoreCase)
                    ? $"Spotify rejected the redirect address. Add {RedirectUri} to your app's Redirect URIs in the Spotify Developer Dashboard."
                    : $"Spotify sign-in failed ({(int)response.StatusCode}).", (int)response.StatusCode);
            }

            using var doc = JsonDocument.Parse(body);
            _userToken = Str(doc.RootElement, "access_token") ?? throw new SpotifyException("Spotify returned an invalid token response.");
            var expires = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var sec) ? sec : 3600;
            _userTokenExpiresUtc = DateTime.UtcNow.AddSeconds(expires - 60);
            // Spotify may rotate the refresh token.
            if (Str(doc.RootElement, "refresh_token") is { } newRefresh)
                await settings.UpdateAsync(s => s.SpotifyRefreshTokenProtected = SecretProtector.Protect(newRefresh)).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Spotify user token request failed");
            throw new SpotifyException("Could not reach Spotify. Check your internet connection.");
        }
        finally
        {
            _userTokenGate.Release();
        }
    }

    /// <summary>Accepts loopback connections until the browser requests /callback; answers with a small page.</summary>
    private static async Task<Dictionary<string, string>> WaitForCallbackAsync(TcpListener listener, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            await using var stream = client.GetStream();
            var buffer = new byte[8192];
            var read = 0;
            try
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readTimeout.CancelAfter(ConnectionReadTimeout);
                while (read < buffer.Length)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(read), readTimeout.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    read += n;
                    if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
            }
            catch (Exception ex) when ((ex is OperationCanceledException && !ct.IsCancellationRequested) || ex is IOException)
            {
                // Browsers open spare (preconnect) sockets that never send a request, or drop them.
                // That one connection is abandoned; the sign-in keeps waiting for the real callback.
                continue;
            }

            if (read == 0) continue;
            var requestLine = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n")[0];
            var target = requestLine.Split(' ') is { Length: >= 2 } parts ? parts[1] : string.Empty;
            var isCallback = target.StartsWith("/callback", StringComparison.Ordinal);
            var query = ParseQuery(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : string.Empty);
            var ok = isCallback && query.ContainsKey("code");
            var html = isCallback
                ? $"<!doctype html><meta charset=utf-8><title>Sonic</title><body style=\"font-family:Segoe UI,sans-serif;background:#1c1c1c;color:#fff;display:grid;place-items:center;height:90vh\"><div style=\"text-align:center\"><h2>{(ok ? "Sonic is connected to Spotify" : "Sign-in didn't finish")}</h2><p>You can close this tab and return to Sonic.</p></div>"
                : "<!doctype html><title>Sonic</title>";
            var bytes = Encoding.UTF8.GetBytes(html);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(isCallback ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            if (isCallback) return query;
        }
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => Uri.UnescapeDataString(p[0]))
            .ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.First()[1].Replace('+', ' ')));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
