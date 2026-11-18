using System.Globalization;
using System.Net;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;

namespace Sonic.Core.Helpers;

/// <summary>Speed limit, proxy and SponsorBlock options translated into yt-dlp arguments.</summary>
public static class NetworkOptions
{
    private static readonly string[] ProxySchemes = ["http", "https", "socks4", "socks4a", "socks5", "socks5h"];

    public const string VideoSponsorCategories = "sponsor,selfpromo,interaction";

    /// <summary>Returns an error message, or null when the proxy is empty or valid.</summary>
    public static string? ValidateProxy(string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy)) return null;
        if (!Uri.TryCreate(proxy.Trim(), UriKind.Absolute, out var uri) || !ProxySchemes.Contains(uri.Scheme))
            return "Use a full proxy address such as http://127.0.0.1:8080 or socks5://127.0.0.1:1080.";
        if (string.IsNullOrEmpty(uri.Host) || uri.Port <= 0) return "The proxy address needs a host and a port.";
        return null;
    }

    /// <summary>Arguments applied to every yt-dlp run.</summary>
    public static IEnumerable<string> CommonArguments(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.ProxyUrl))
        {
            yield return "--proxy";
            yield return s.ProxyUrl;
        }
    }

    /// <summary>Arguments applied to downloads only.</summary>
    public static IEnumerable<string> DownloadArguments(AppSettings s, MediaKind kind)
    {
        if (s.SpeedLimitKBps > 0)
        {
            yield return "--limit-rate";
            yield return s.SpeedLimitKBps.ToString(CultureInfo.InvariantCulture) + "K";
        }

        if (kind == MediaKind.Audio && s.SponsorBlockAudio)
        {
            // Community-submitted "non-music" segments in music videos: intros, skits, talking.
            yield return "--sponsorblock-remove";
            yield return "music_offtopic";
        }
        else if (kind == MediaKind.Video && s.SponsorBlockVideo != SponsorBlockMode.Off)
        {
            yield return s.SponsorBlockVideo == SponsorBlockMode.Remove ? "--sponsorblock-remove" : "--sponsorblock-mark";
            yield return VideoSponsorCategories;
        }
    }
}

/// <summary>
/// <see cref="IWebProxy"/> that follows the proxy setting live, so Sonic's own HTTP requests (tool downloads,
/// Spotify, artwork) use the same proxy as yt-dlp without restarting.
/// </summary>
public sealed class SettingsWebProxy(ISettingsService settings) : IWebProxy
{
    public ICredentials? Credentials { get; set; }

    public Uri? GetProxy(Uri destination) =>
        // SocketsHttpHandler supports http, https and socks4/4a/5 proxies.
        settings.Current.ProxyUrl is { Length: > 0 } p && Uri.TryCreate(p, UriKind.Absolute, out var uri) ? uri : null;

    public bool IsBypassed(Uri host) => GetProxy(host) is null || host.IsLoopback;
}
