using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Sonic.Core.Abstractions;
using Sonic.Core.Services;
using Xunit;

namespace Sonic.Core.Tests;

/// <summary>Spotify account sign-in (PKCE + loopback redirect) and Liked Songs paging, against a fake Spotify.</summary>
public sealed class SpotifyUserTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("sonic-sp").FullName;
    private readonly FakeSpotify _fake = new();
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSonicCore("Sonic-Tests/1.0");
        services.AddSingleton<IAppPaths>(new AppPaths(_root));
        services.AddSingleton<IHttpClientFactory>(new FakeFactory(_fake));
        _sp = services.BuildServiceProvider();
        var settings = _sp.GetRequiredService<ISettingsService>();
        await settings.LoadAsync();
        await settings.UpdateAsync(s => s.SpotifyClientId = "test-client");
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Signs_in_through_the_loopback_redirect_and_reads_liked_songs()
    {
        var spotify = _sp.GetRequiredService<ISpotifyService>();
        string? page = null;

        await spotify.SignInAsync(async authorize =>
        {
            // Act as the browser: Spotify redirects back to Sonic with the code and the same state.
            var query = authorize.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Equal(SpotifyService.RedirectUri, query["redirect_uri"]);
            Assert.Contains("user-library-read", query["scope"]);
            _fake.ExpectedChallengeFrom = query["code_challenge"];
            _ = Task.Run(async () =>
            {
                using var browser = new HttpClient();
                page = await browser.GetStringAsync($"{SpotifyService.RedirectUri}?code=the-code&state={query["state"]}");
            });
            await Task.CompletedTask;
        });

        Assert.True(spotify.IsSignedIn);
        Assert.Equal("Tester", spotify.SignedInUser);
        Assert.True(_fake.VerifierMatchedChallenge);
        for (var i = 0; i < 50 && page is null; i++) await Task.Delay(20);
        Assert.Contains("connected to Spotify", page);

        var liked = await spotify.GetLikedSongsAsync();
        Assert.Equal(3, liked.Total);
        Assert.Equal(["Song A", "Song B", "Song C"], liked.Tracks.Select(t => t.Name));
        Assert.Equal(new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc), liked.Tracks[0].AddedAt);
        Assert.Equal("Artist A", liked.Tracks[0].ArtistText);

        await spotify.SignOutAsync();
        Assert.False(spotify.IsSignedIn);
        await Assert.ThrowsAsync<SpotifyException>(() => spotify.GetLikedSongsAsync());
    }

    [Fact]
    public async Task A_silent_preconnect_socket_does_not_abort_the_sign_in()
    {
        SpotifyService.ConnectionReadTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var spotify = _sp.GetRequiredService<ISpotifyService>();
            using var idle = new System.Net.Sockets.TcpClient();
            await spotify.SignInAsync(async authorize =>
            {
                var query = authorize.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                _fake.ExpectedChallengeFrom = query["code_challenge"];
                // Like Chrome: a spare connection that never sends a request is accepted first…
                await idle.ConnectAsync(System.Net.IPAddress.Loopback, SpotifyService.RedirectPort);
                // …and the real callback arrives on another one.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    using var browser = new HttpClient();
                    await browser.GetStringAsync($"{SpotifyService.RedirectUri}?code=the-code&state={query["state"]}");
                });
            });

            Assert.True(spotify.IsSignedIn);
            Assert.True(_fake.VerifierMatchedChallenge);
        }
        finally
        {
            SpotifyService.ConnectionReadTimeout = TimeSpan.FromSeconds(10);
        }
    }

    [Fact]
    public async Task Canceled_consent_reports_a_clear_error()
    {
        var spotify = _sp.GetRequiredService<ISpotifyService>();
        var ex = await Assert.ThrowsAsync<SpotifyException>(() => spotify.SignInAsync(authorize =>
        {
            var state = authorize.Query.Split('&').First(p => p.StartsWith("state=", StringComparison.Ordinal))[6..];
            _ = Task.Run(() => new HttpClient().GetStringAsync($"{SpotifyService.RedirectUri}?error=access_denied&state={state}"));
            return Task.CompletedTask;
        }));
        Assert.Contains("canceled", ex.Message);
        Assert.False(spotify.IsSignedIn);
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeSpotify : HttpMessageHandler
    {
        public string? ExpectedChallengeFrom { get; set; }
        public bool VerifierMatchedChallenge { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == "https://accounts.spotify.com/api/token")
            {
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                var fields = form.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                if (fields["grant_type"] == "authorization_code")
                {
                    var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(fields["code_verifier"])))
                        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                    VerifierMatchedChallenge = challenge == ExpectedChallengeFrom && fields["code"] == "the-code";
                }

                return Json("""{"access_token":"AT","token_type":"Bearer","expires_in":3600,"refresh_token":"RT"}""");
            }

            Assert.Equal("Bearer AT", request.Headers.Authorization?.ToString());
            return url switch
            {
                "https://api.spotify.com/v1/me" => Json("""{"id":"u1","display_name":"Tester"}"""),
                "https://api.spotify.com/v1/me/tracks?limit=50" => Json("""
                    {"total":3,"next":"https://api.spotify.com/v1/me/tracks?offset=2&limit=50","items":[
                      {"added_at":"2026-11-18T10:00:00Z","track":{"id":"a","name":"Song A","duration_ms":200000,"artists":[{"name":"Artist A"}],"album":{"name":"Album A","release_date":"2020-01-01","images":[]}}},
                      {"added_at":"2026-11-17T10:00:00Z","track":{"id":"b","name":"Song B","duration_ms":180000,"artists":[{"name":"Artist B"}],"album":{"name":"Album B","images":[]}}}]}
                    """),
                "https://api.spotify.com/v1/me/tracks?offset=2&limit=50" => Json("""
                    {"total":3,"next":null,"items":[
                      {"added_at":"2026-01-01T00:00:00Z","track":{"id":"c","name":"Song C","duration_ms":100000,"artists":[{"name":"Artist C"}],"album":{"name":"Album C","images":[]}}}]}
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
