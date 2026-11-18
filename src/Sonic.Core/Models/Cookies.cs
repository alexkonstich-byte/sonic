namespace Sonic.Core.Models;

public enum CookieMode
{
    /// <summary>yt-dlp runs anonymously.</summary>
    None = 0,
    /// <summary>A snapshot imported from a browser or cookies.txt, stored encrypted by Sonic.</summary>
    Imported = 1,
    /// <summary>yt-dlp reads the browser's cookies on every run.</summary>
    Browser = 2,
}

/// <summary>A browser profile Sonic found on this PC.</summary>
public sealed record BrowserProfile(
    string Browser,
    string ProfileName,
    string ProfilePath,
    /// <summary>Value for yt-dlp's <c>--cookies-from-browser</c>, e.g. "firefox:C:\…\profile".</summary>
    string Spec,
    bool IsChromium,
    bool IsRunning,
    bool IsExperimental)
{
    public string DisplayName => ProfileName.Length == 0 ? Browser : $"{Browser} — {ProfileName}";
}

public sealed record CookieSummary(int Count, int YouTubeCount, bool SignedInToGoogle, IReadOnlyList<string> TopDomains);

public sealed record CookieStatus(CookieMode Mode, CookieSummary? Imported, string? Source, DateTime? ImportedAtUtc, string? BrowserName);

public sealed class CookieException(string message) : Exception(message);
