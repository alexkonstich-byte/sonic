using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sonic.Core.Services;
using Xunit;

namespace Sonic.Core.Tests;

public class CoverArtTests
{
    private static CoverCandidate Candidate(string artist, string title, string? album = null, double? duration = null) =>
        new("Test", artist, title, album, null, duration, "https://example/x.jpg", "https://example/s.jpg", 0);

    [Fact]
    public void Exact_match_scores_high()
    {
        var score = CoverArtService.Score(Candidate("Rick Astley", "Never Gonna Give You Up", "Whenever You Need Somebody", 213), "Rick Astley", "Never Gonna Give You Up", 212);
        Assert.True(score >= CoverArtService.ConfidentScore, $"score {score}");
    }

    [Fact]
    public void Video_title_noise_still_matches()
    {
        var score = CoverArtService.Score(Candidate("Rick Astley", "Never Gonna Give You Up"), "Rick Astley", "Never Gonna Give You Up (Official Music Video)", null);
        Assert.True(score >= CoverArtService.ConfidentScore, $"score {score}");
    }

    [Fact]
    public void Karaoke_and_wrong_artist_are_rejected()
    {
        var karaoke = CoverArtService.Score(Candidate("Karaoke Hits Band", "Never Gonna Give You Up (Karaoke Version)"), "Rick Astley", "Never Gonna Give You Up", null);
        var other = CoverArtService.Score(Candidate("Cake", "Never Gonna Give You Up"), "Rick Astley", "Never Gonna Give You Up", null);
        Assert.True(karaoke < CoverArtService.ConfidentScore, $"karaoke {karaoke}");
        Assert.True(other < CoverArtService.ConfidentScore, $"other {other}");
    }

    [Fact]
    public void Similarity_ignores_case_accents_and_feat()
    {
        Assert.Equal(1, CoverArtService.Similarity("Beyoncé", "beyonce"));
        Assert.True(CoverArtService.Similarity("Daft Punk feat. Pharrell Williams", "Daft Punk") > 0.7);
    }

    [SkippableFact]
    public async Task Finds_real_cover_online()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("SONIC_INTEGRATION") == "1", "Set SONIC_INTEGRATION=1 to run network tests.");
        var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var service = new CoverArtService(services.GetRequiredService<IHttpClientFactory>(), NullLogger<CoverArtService>.Instance);
        var results = await service.SearchAsync("Rick Astley", "Never Gonna Give You Up", 213);
        Assert.NotEmpty(results);
        Assert.True(results[0].Score >= CoverArtService.ConfidentScore, $"best {results[0].Description} {results[0].Score}");
        var bytes = await service.DownloadAsync(results[0]);
        Assert.True(bytes.Length > 20_000);
    }
}
