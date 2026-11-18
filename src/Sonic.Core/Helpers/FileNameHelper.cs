using System.Text;
using Sonic.Core.Models;

namespace Sonic.Core.Helpers;

public static class FileNameHelper
{
    public const string DefaultTemplate = "{artist} - {title}";

    public static readonly string[] SupportedTokens =
        ["{title}", "{artist}", "{album}", "{album_artist}", "{track}", "{year}", "{genre}", "{uploader}", "{id}"];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const int MaxSegmentLength = 120;

    /// <summary>Makes a single path segment safe for Windows file systems.</summary>
    public static string SanitizeSegment(string? value, string fallback = "Unknown")
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '/' or '\\') sb.Append('-');
            else if (c == ':') sb.Append(" -");
            else if (c == '"') sb.Append('\'');
            else if (c is '?' or '*') { }
            else if (c is '<') sb.Append('(');
            else if (c is '>') sb.Append(')');
            else if (c == '|') sb.Append('-');
            else if (Array.IndexOf(invalid, c) >= 0 || char.IsControl(c)) { }
            else sb.Append(c);
        }

        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        while (result.Contains("  ", StringComparison.Ordinal)) result = result.Replace("  ", " ", StringComparison.Ordinal);
        if (result.Length > MaxSegmentLength) result = result[..MaxSegmentLength].TrimEnd('.', ' ');
        if (result.Length == 0) return fallback;
        if (ReservedNames.Contains(result) || ReservedNames.Contains(Path.GetFileNameWithoutExtension(result))) result = "_" + result;
        return result;
    }

    public const string DefaultVideoTemplate = "{uploader} - {title}";

    public static readonly string[] VideoTokens =
        ["{title}", "{uploader}", "{id}", "{date}", "{year}", "{resolution}"];

    public static IReadOnlyList<string> TokensFor(MediaKind kind) => kind == MediaKind.Video ? VideoTokens : SupportedTokens;

    public static string DefaultTemplateFor(MediaKind kind) => kind == MediaKind.Video ? DefaultVideoTemplate : DefaultTemplate;

    /// <summary>Validates a template for the given kind of download.</summary>
    public static string? ValidateTemplate(string? template, MediaKind kind) => ValidateTemplate(template, TokensFor(kind));

    /// <summary>Validates a filename template. Returns an error message, or null when valid.</summary>
    public static string? ValidateTemplate(string? template) => ValidateTemplate(template, SupportedTokens);

    private static string? ValidateTemplate(string? template, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(template)) return "The template cannot be empty.";
        if (Path.IsPathRooted(template)) return "The template must be a relative path.";
        if (!template.Contains("{title}", StringComparison.OrdinalIgnoreCase) && !template.Contains("{id}", StringComparison.OrdinalIgnoreCase))
            return "Include {title} or {id} so each file gets a distinct name.";
        var segments = template.Split('/', '\\');
        if (segments.Any(s => s.Trim() is "" or "." or "..")) return "The template contains an empty or relative folder name.";
        var i = 0;
        while ((i = template.IndexOf('{', i)) >= 0)
        {
            var end = template.IndexOf('}', i);
            if (end < 0) return "A token is missing its closing brace.";
            var token = template[i..(end + 1)].ToLowerInvariant();
            if (!tokens.Contains(token)) return $"Unknown token {token}. Available: {string.Join(' ', tokens)}";
            i = end + 1;
        }

        return null;
    }

    /// <summary>Expands a template into a relative path (without extension). Folder separators are allowed.</summary>
    public static string ExpandTemplate(string? template, TrackMetadata metadata, string? uploader, string? id)
    {
        if (ValidateTemplate(template) is not null) template = DefaultTemplate;
        var title = string.IsNullOrWhiteSpace(metadata.Title) ? id ?? "Untitled" : metadata.Title;
        var artist = metadata.Artist ?? uploader;
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["{title}"] = title,
            ["{artist}"] = artist ?? "Unknown Artist",
            ["{album}"] = metadata.Album ?? "Unknown Album",
            ["{album_artist}"] = metadata.AlbumArtist ?? artist ?? "Unknown Artist",
            ["{track}"] = metadata.TrackNumber is { } n ? n.ToString("00") : "00",
            ["{year}"] = metadata.Year?.ToString() ?? "Unknown Year",
            ["{genre}"] = metadata.Genre ?? "Unknown Genre",
            ["{uploader}"] = uploader ?? artist ?? "Unknown",
            ["{id}"] = id ?? "unknown",
        };

        return Expand(template!, values);
    }

    /// <summary>Expands a video template. Tokens: {title} {uploader} {id} {date} {year} {resolution}.</summary>
    public static string ExpandVideoTemplate(string? template, string? title, string? uploader, string? id, string? uploadDate, int? height)
    {
        if (ValidateTemplate(template, MediaKind.Video) is not null) template = DefaultVideoTemplate;
        string? date = null, year = null;
        if (uploadDate is { Length: 8 } d && d.All(char.IsAsciiDigit))
        {
            date = $"{d[..4]}-{d[4..6]}-{d[6..]}";
            year = d[..4];
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["{title}"] = string.IsNullOrWhiteSpace(title) ? id ?? "Untitled" : title,
            ["{uploader}"] = uploader ?? "Unknown",
            ["{id}"] = id ?? "unknown",
            ["{date}"] = date ?? "Unknown date",
            ["{year}"] = year ?? "Unknown year",
            ["{resolution}"] = height is > 0 ? $"{height}p" : "video",
        };
        return Expand(template!, values);
    }

    private static string Expand(string template, Dictionary<string, string?> values)
    {
        var segments = template.Split('/', '\\').Select(segment =>
        {
            var expanded = segment;
            foreach (var (token, value) in values)
                expanded = expanded.Replace(token, SanitizeSegment(value, "Unknown"), StringComparison.OrdinalIgnoreCase);
            return SanitizeSegment(expanded, "Untitled");
        });
        return Path.Combine([.. segments]);
    }

    /// <summary>
    /// Returns <paramref name="desiredPath"/> if no file exists there; otherwise appends " (2)", " (3)"…
    /// so existing files are never overwritten.
    /// </summary>
    public static string GetUniquePath(string desiredPath)
    {
        if (!File.Exists(desiredPath)) return desiredPath;
        var dir = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var ext = Path.GetExtension(desiredPath);
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    /// <summary>Checks that a directory path is absolute, well-formed and (if it exists) writable.</summary>
    public static string? ValidateDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Choose a folder.";
        try
        {
            if (!Path.IsPathFullyQualified(path)) return "Use a full path, for example C:\\Users\\you\\Music.";
            _ = Path.GetFullPath(path);
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return "The path contains invalid characters.";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "The path is not valid.";
        }

        return null;
    }

    public static string GetExtension(AudioFormat format) => format switch
    {
        AudioFormat.Mp3 => ".mp3",
        AudioFormat.Flac => ".flac",
        AudioFormat.Wav => ".wav",
        AudioFormat.M4a => ".m4a",
        AudioFormat.Opus => ".opus",
        AudioFormat.Ogg => ".ogg",
        _ => string.Empty,
    };

    public static readonly string[] AudioExtensions =
        [".mp3", ".flac", ".wav", ".m4a", ".aac", ".opus", ".ogg", ".oga", ".wma", ".aiff", ".aif", ".ape", ".wv", ".mka"];

    public static bool IsAudioFile(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
