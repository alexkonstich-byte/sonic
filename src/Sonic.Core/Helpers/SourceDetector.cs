using Sonic.Core.Models;

namespace Sonic.Core.Helpers;

public static class SourceDetector
{
    /// <summary>Platforms whose content is DRM-protected streaming; Sonic only reads their metadata.</summary>
    private static readonly string[] ProtectedStreamingHosts =
        ["music.apple.com", "deezer.com", "tidal.com", "music.amazon.", "music.yandex."];

    public static bool TryNormalizeUrl(string? input, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var text = input.Trim();
        if (text.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
        {
            uri = new Uri("https://open.spotify.com/" + text["spotify:".Length..].Replace(':', '/'));
            return true;
        }

        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (!parsed.Host.Contains('.')) return false;
        uri = parsed;
        return true;
    }

    public static SourcePlatform Detect(string? input)
    {
        if (!TryNormalizeUrl(input, out var uri)) return SourcePlatform.Unknown;
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (host.StartsWith("m.", StringComparison.Ordinal)) host = host[2..];

        return host switch
        {
            "music.youtube.com" => SourcePlatform.YouTubeMusic,
            "youtube.com" or "youtu.be" or "youtube-nocookie.com" => SourcePlatform.YouTube,
            "vk.com" or "vk.ru" or "vkvideo.ru" or "vk.video" => SourcePlatform.VK,
            "rutube.ru" => SourcePlatform.Rutube,
            "soundcloud.com" or "on.soundcloud.com" => SourcePlatform.SoundCloud,
            "vimeo.com" or "player.vimeo.com" => SourcePlatform.Vimeo,
            "open.spotify.com" or "spotify.com" or "spotify.link" => SourcePlatform.Spotify,
            _ when host.EndsWith(".bandcamp.com", StringComparison.Ordinal) || host == "bandcamp.com" => SourcePlatform.Bandcamp,
            _ when host.EndsWith(".youtube.com", StringComparison.Ordinal) => SourcePlatform.YouTube,
            _ => SourcePlatform.Other,
        };
    }

    /// <summary>
    /// A key that identifies the same media across URL variants (youtu.be/x, youtube.com/watch?v=x&amp;t=1,
    /// music.youtube.com/watch?v=x all map to "youtube:x"). Used to detect repeat downloads.
    /// </summary>
    public static string CanonicalKey(string? url)
    {
        if (!TryNormalizeUrl(url, out var uri)) return url?.Trim().ToLowerInvariant() ?? string.Empty;
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (host.StartsWith("m.", StringComparison.Ordinal)) host = host[2..];

        string? Query(string name) => uri.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && p[0] == name)
            .Select(p => Uri.UnescapeDataString(p[1]))
            .FirstOrDefault();

        if (host == "youtu.be") return "youtube:" + uri.AbsolutePath.Trim('/');
        if (host is "youtube.com" or "music.youtube.com")
        {
            if (Query("v") is { Length: > 0 } v) return "youtube:" + v;
            var path = uri.AbsolutePath;
            foreach (var prefix in new[] { "/shorts/", "/live/", "/embed/" })
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "youtube:" + path[prefix.Length..].Trim('/');
            }
        }

        // Other sites: scheme-less, without query/fragment and trailing slash.
        return (host + uri.AbsolutePath).TrimEnd('/').ToLowerInvariant();
    }

    public static bool IsProtectedStreamingService(string? input)
    {
        if (!TryNormalizeUrl(input, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        return ProtectedStreamingHosts.Any(h => host.Contains(h, StringComparison.Ordinal));
    }

    public static string GetDisplayName(SourcePlatform platform) => platform switch
    {
        SourcePlatform.YouTube => "YouTube",
        SourcePlatform.YouTubeMusic => "YouTube Music",
        SourcePlatform.VK => "VK",
        SourcePlatform.Rutube => "Rutube",
        SourcePlatform.SoundCloud => "SoundCloud",
        SourcePlatform.Bandcamp => "Bandcamp",
        SourcePlatform.Vimeo => "Vimeo",
        SourcePlatform.Spotify => "Spotify",
        SourcePlatform.Other => "Web",
        _ => "Unknown",
    };

    /// <summary>Maps a yt-dlp extractor key (e.g. "Youtube", "VKPlaylist") to a platform.</summary>
    public static SourcePlatform FromExtractor(string? extractor, string? url)
    {
        var fromUrl = Detect(url);
        if (fromUrl is not SourcePlatform.Unknown and not SourcePlatform.Other) return fromUrl;
        if (string.IsNullOrEmpty(extractor)) return fromUrl;
        var e = extractor.ToLowerInvariant();
        if (e.StartsWith("youtube", StringComparison.Ordinal)) return SourcePlatform.YouTube;
        if (e.StartsWith("vk", StringComparison.Ordinal)) return SourcePlatform.VK;
        if (e.StartsWith("rutube", StringComparison.Ordinal)) return SourcePlatform.Rutube;
        if (e.StartsWith("soundcloud", StringComparison.Ordinal)) return SourcePlatform.SoundCloud;
        if (e.StartsWith("bandcamp", StringComparison.Ordinal)) return SourcePlatform.Bandcamp;
        if (e.StartsWith("vimeo", StringComparison.Ordinal)) return SourcePlatform.Vimeo;
        return fromUrl == SourcePlatform.Unknown ? SourcePlatform.Other : fromUrl;
    }
}
