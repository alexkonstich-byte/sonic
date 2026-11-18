using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;

namespace Sonic.Core.Services;

/// <summary>A square album cover found in a public catalog.</summary>
public sealed record CoverCandidate(
    string Source,
    string Artist,
    string Title,
    string? Album,
    int? Year,
    double? DurationSeconds,
    string ImageUrl,
    string PreviewUrl,
    double Score)
{
    public string Description => string.Join(" · ", new[] { Artist, Album, Year?.ToString(CultureInfo.InvariantCulture), Source }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// Finds album covers for a song in the iTunes Search API and Deezer's public API (no keys, metadata only).
/// Results are scored by how well artist, title and length match, so callers can apply only confident matches.
/// </summary>
public sealed class CoverArtService(IHttpClientFactory httpFactory, ILogger<CoverArtService> logger) : ICoverArtService
{
    /// <summary>Score from which a match is applied without asking.</summary>
    public const double ConfidentScore = 0.8;

    private static readonly string[] UnwantedWords = ["karaoke", "tribute", "instrumental", "cover", "remix", "8-bit", "lullaby", "made famous", "originally performed"];

    public async Task<IReadOnlyList<CoverCandidate>> SearchAsync(string? artist, string title, double? durationSeconds, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return [];
        var query = string.Join(' ', new[] { artist, title }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var tasks = new[] { SearchITunesAsync(query, cancellationToken), SearchDeezerAsync(query, cancellationToken) };
        var all = (await Task.WhenAll(tasks).ConfigureAwait(false)).SelectMany(r => r);

        return all
            .Select(c => c with { Score = Score(c, artist, title, durationSeconds) })
            .OrderByDescending(c => c.Score)
            // The same album from both catalogs: keep the better scored one.
            .DistinctBy(c => (Normalize(c.Artist), Normalize(c.Album ?? c.Title)))
            .Take(12)
            .ToList();
    }

    public async Task<byte[]> DownloadAsync(CoverCandidate candidate, CancellationToken cancellationToken = default)
    {
        var http = httpFactory.CreateClient(DependencyService.HttpClientName);
        return await http.GetByteArrayAsync(candidate.ImageUrl, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<CoverCandidate>> SearchITunesAsync(string query, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient(DependencyService.HttpClientName);
            var url = $"https://itunes.apple.com/search?media=music&entity=song&limit=10&term={Uri.EscapeDataString(query)}";
            using var doc = await http.GetFromJsonAsync<JsonDocument>(url, ct).ConfigureAwait(false);
            if (doc is null || !doc.RootElement.TryGetProperty("results", out var results)) return [];
            var list = new List<CoverCandidate>();
            foreach (var r in results.EnumerateArray())
            {
                var art = Str(r, "artworkUrl100");
                if (art is null) continue;
                int? year = Str(r, "releaseDate") is { Length: >= 4 } date && int.TryParse(date[..4], out var y) ? y : null;
                double? duration = r.TryGetProperty("trackTimeMillis", out var ms) && ms.TryGetDouble(out var m) ? m / 1000 : null;
                list.Add(new CoverCandidate("iTunes", Str(r, "artistName") ?? string.Empty, Str(r, "trackName") ?? string.Empty, Str(r, "collectionName"),
                    year, duration, art.Replace("100x100bb", "1000x1000bb", StringComparison.Ordinal), art.Replace("100x100bb", "300x300bb", StringComparison.Ordinal), 0));
            }

            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "iTunes cover search failed");
            return [];
        }
    }

    private async Task<IReadOnlyList<CoverCandidate>> SearchDeezerAsync(string query, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient(DependencyService.HttpClientName);
            var url = $"https://api.deezer.com/search?limit=10&q={Uri.EscapeDataString(query)}";
            using var doc = await http.GetFromJsonAsync<JsonDocument>(url, ct).ConfigureAwait(false);
            if (doc is null || !doc.RootElement.TryGetProperty("data", out var data)) return [];
            var list = new List<CoverCandidate>();
            foreach (var r in data.EnumerateArray())
            {
                if (!r.TryGetProperty("album", out var album)) continue;
                var art = Str(album, "cover_xl") ?? Str(album, "cover_big");
                if (art is null) continue;
                var artist = r.TryGetProperty("artist", out var a) ? Str(a, "name") : null;
                double? duration = r.TryGetProperty("duration", out var d) && d.TryGetDouble(out var s) ? s : null;
                list.Add(new CoverCandidate("Deezer", artist ?? string.Empty, Str(r, "title") ?? string.Empty, Str(album, "title"), null, duration,
                    art, Str(album, "cover_medium") ?? art, 0));
            }

            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Deezer cover search failed");
            return [];
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    /// <summary>0..1: title and artist similarity, with a bonus for matching length and penalties for karaoke/cover versions.</summary>
    public static double Score(CoverCandidate c, string? artist, string title, double? durationSeconds)
    {
        var titleScore = Similarity(TitleParser.Clean(title), c.Title);
        var artistScore = string.IsNullOrWhiteSpace(artist) ? 0.6 : Similarity(artist, c.Artist);
        var score = titleScore * 0.55 + artistScore * 0.45;

        if (durationSeconds is > 0 && c.DurationSeconds is > 0)
        {
            var diff = Math.Abs(durationSeconds.Value - c.DurationSeconds.Value);
            score += diff <= 4 ? 0.08 : diff > 30 ? -0.12 : 0;
        }

        var wanted = Normalize(title);
        var found = Normalize(c.Title + " " + c.Album);
        if (UnwantedWords.Any(w => found.Contains(w, StringComparison.Ordinal) && !wanted.Contains(w, StringComparison.Ordinal))) score -= 0.35;

        return Math.Clamp(score, 0, 1);
    }

    /// <summary>Token overlap that also accepts "feat." extras and different word order.</summary>
    public static double Similarity(string? a, string? b)
    {
        var ta = Tokens(a);
        var tb = Tokens(b);
        if (ta.Count == 0 || tb.Count == 0) return 0;
        if (string.Join(' ', ta) == string.Join(' ', tb)) return 1;
        var common = ta.Intersect(tb).Count();
        // How much of the shorter name is found in the longer one, slightly penalized by extra words.
        var coverage = (double)common / Math.Min(ta.Count, tb.Count);
        var precision = (double)common / Math.Max(ta.Count, tb.Count);
        return coverage * 0.75 + precision * 0.25;
    }

    private static readonly HashSet<string> StopWords = ["the", "a", "feat", "ft", "featuring", "and", "x", "with"];

    private static List<string> Tokens(string? s) =>
        Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !StopWords.Contains(t)).Distinct().ToList();

    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var formD = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
