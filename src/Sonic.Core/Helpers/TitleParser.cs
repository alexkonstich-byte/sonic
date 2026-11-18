using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Sonic.Core.Helpers;

/// <summary>Heuristics for turning video titles into clean track metadata.</summary>
public static partial class TitleParser
{
    // Bracketed noise commonly appended to music video titles.
    [GeneratedRegex(
        @"\s*[\(\[\{](?:[^\)\]\}]*?\b(?:official|lyrics?|lyric video|audio|video|visuali[sz]er|music video|mv|hd|hq|4k|1080p|720p|explicit|clean|remaster(?:ed)?(?: \d{4})?)\b[^\)\]\}]*)[\)\]\}]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoiseBrackets();

    [GeneratedRegex(@"\s*\|\s*.*$", RegexOptions.CultureInvariant)]
    private static partial Regex PipeSuffix();

    [GeneratedRegex(@"\s+-\s+Topic$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TopicSuffix();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"\b(?:feat\.?|ft\.?|featuring)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Featuring();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex(@"^(?:official\b|(?:the\s+)?(?:official\s+)?(?:trailer|teaser|short film|full movie|full episode|documentary|gameplay|tutorial|review|interview|highlights)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DescriptiveSuffix();

    private static readonly string[] Separators = [" - ", " – ", " — ", " -- ", " ~ "];

    /// <summary>Removes common noise like "(Official Video)" or "[Lyrics]" from a title.</summary>
    public static string Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var result = NoiseBrackets().Replace(title, string.Empty);
        result = PipeSuffix().Replace(result, string.Empty);
        result = MultiSpace().Replace(result, " ").Trim();
        result = result.Trim('-', '–', '—', ' ', '"');
        return result.Length == 0 ? title.Trim() : result;
    }

    /// <summary>Removes the " - Topic" suffix YouTube adds to auto-generated artist channels.</summary>
    public static string? CleanUploader(string? uploader) =>
        string.IsNullOrWhiteSpace(uploader) ? uploader : TopicSuffix().Replace(uploader.Trim(), string.Empty);

    /// <summary>Splits "Artist - Title" style video titles. Returns false when no separator is present.</summary>
    public static bool TrySplitArtistTitle(string? rawTitle, out string artist, out string title)
    {
        artist = string.Empty;
        title = string.Empty;
        if (string.IsNullOrWhiteSpace(rawTitle)) return false;
        var cleaned = Clean(rawTitle);
        foreach (var sep in Separators)
        {
            var idx = cleaned.IndexOf(sep, StringComparison.Ordinal);
            if (idx <= 0) continue;
            var a = cleaned[..idx].Trim().Trim('"');
            var t = cleaned[(idx + sep.Length)..].Trim().Trim('"');
            if (a.Length == 0 || t.Length == 0) continue;
            // "Movie Name - Official Trailer" style titles describe the video rather than naming an artist.
            if (DescriptiveSuffix().IsMatch(t)) return false;
            artist = a;
            title = t;
            return true;
        }

        return false;
    }

    /// <summary>Builds a normalized key ("artist|title") used to detect duplicates regardless of punctuation/case.</summary>
    public static string NormalizeKey(string? artist, string? title)
    {
        static string Norm(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var cleaned = Clean(s);
            cleaned = Featuring().Split(cleaned)[0];
            var decomposed = cleaned.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            }

            return NonAlphanumeric().Replace(sb.ToString().ToLowerInvariant(), " ").Trim();
        }

        var firstArtist = artist?.Split([",", "&", ";", " x ", " X "], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return $"{Norm(firstArtist)}|{Norm(title)}";
    }
}
