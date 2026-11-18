using System.Globalization;
using System.Text;

namespace Sonic.Core.Helpers;

/// <summary>One line of a batch list: either a link to download directly or a search query.</summary>
public sealed record BatchEntry
{
    public string? Url { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public double? DurationSeconds { get; init; }
    public required string Display { get; init; }

    public bool IsLink => Url is not null;

    /// <summary>Text to search for when there's no link.</summary>
    public string Query => Artist is { Length: > 0 } a && Title is { Length: > 0 } t ? $"{a} - {t}" : Title ?? Display;
}

/// <summary>
/// Parses pasted text, .txt, .csv (including Exportify/Spotify exports) and .m3u/.m3u8 lists into <see cref="BatchEntry"/> items.
/// </summary>
public static class BatchListParser
{
    public const int MaxEntries = 2000;

    public static IReadOnlyList<BatchEntry> ParseFile(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csv" or ".tsv" => ParseCsv(text),
            ".m3u" or ".m3u8" => ParseM3u(text),
            _ => text.TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) ? ParseM3u(text) : ParseLines(text),
        };
    }

    /// <summary>One entry per non-empty line. Lines starting with # or // are comments.</summary>
    public static IReadOnlyList<BatchEntry> ParseLines(string text)
    {
        var result = new List<BatchEntry>();
        foreach (var raw in text.Split(['\r', '\n']))
        {
            var line = raw.Trim().Trim('﻿');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;
            // Numbered lists like "1. Artist - Title" or "01) …".
            line = StripNumbering(line);
            result.Add(FromText(line));
            if (result.Count >= MaxEntries) break;
        }

        return Distinct(result);
    }

    public static IReadOnlyList<BatchEntry> ParseM3u(string text)
    {
        var result = new List<BatchEntry>();
        string? pendingTitle = null;
        double? pendingDuration = null;
        foreach (var raw in text.Split(['\r', '\n']))
        {
            var line = raw.Trim().Trim('﻿');
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = line.IndexOf(',');
                var seconds = line[8..(comma < 0 ? line.Length : comma)];
                pendingDuration = double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;
                pendingTitle = comma >= 0 ? line[(comma + 1)..].Trim() : null;
                continue;
            }

            if (line.StartsWith('#')) continue;
            if (LooksLikeUrl(line))
            {
                result.Add(new BatchEntry { Url = line, Display = pendingTitle ?? line, DurationSeconds = pendingDuration });
            }
            else
            {
                // A local file path from another player: search for its title instead.
                var name = pendingTitle ?? Path.GetFileNameWithoutExtension(line.Replace('/', '\\'));
                if (!string.IsNullOrWhiteSpace(name)) result.Add(FromText(name) with { DurationSeconds = pendingDuration });
            }

            pendingTitle = null;
            pendingDuration = null;
            if (result.Count >= MaxEntries) break;
        }

        return Distinct(result);
    }

    /// <summary>
    /// CSV with or without a header. Recognized columns: url/link, title/track/song/name, artist(s), album, duration.
    /// Durations in "Duration (ms)" columns are converted; "m:ss" values are accepted too.
    /// </summary>
    public static IReadOnlyList<BatchEntry> ParseCsv(string text)
    {
        var rows = ReadCsv(text);
        if (rows.Count == 0) return [];

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToArray();
        // Names are in priority order: exact matches first, then "name (unit)" style headers.
        int Find(params string[] names)
        {
            foreach (var n in names)
            {
                var i = Array.IndexOf(header, n);
                if (i >= 0) return i;
            }

            foreach (var n in names)
            {
                var i = Array.FindIndex(header, h => h.StartsWith(n + " (", StringComparison.Ordinal) || h.StartsWith(n + "(", StringComparison.Ordinal));
                if (i >= 0) return i;
            }

            return -1;
        }
        var url = Find("url", "link", "track url", "spotify url", "youtube url");
        var title = Find("track name", "title", "track", "song", "name", "song name");
        var artist = Find("artist name(s)", "artist names", "artists", "artist", "artist name", "performer");
        var album = Find("album name", "album");
        var duration = Find("duration (ms)", "duration_ms", "duration", "length", "time");
        var hasHeader = url >= 0 || title >= 0 || artist >= 0;

        var result = new List<BatchEntry>();
        foreach (var row in hasHeader ? rows.Skip(1) : rows)
        {
            string? Cell(int i) => i >= 0 && i < row.Length && !string.IsNullOrWhiteSpace(row[i]) ? row[i].Trim() : null;

            if (!hasHeader)
            {
                // Headerless: a URL anywhere in the row wins, otherwise "first - second" cell.
                var link = row.FirstOrDefault(c => LooksLikeUrl(c.Trim()));
                if (link is not null) result.Add(new BatchEntry { Url = link.Trim(), Display = link.Trim() });
                else
                {
                    var cells = row.Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
                    if (cells.Count >= 2) result.Add(new BatchEntry { Artist = cells[0], Title = cells[1], Display = $"{cells[0]} - {cells[1]}" });
                    else if (cells.Count == 1) result.Add(FromText(cells[0]));
                }
            }
            else
            {
                var u = Cell(url);
                var t = Cell(title);
                // Spotify exports list several artists separated by ";" or ","; the first one is the main artist.
                var a = Cell(artist)?.Split([';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                var al = Cell(album);
                var dur = ParseDuration(Cell(duration), header.ElementAtOrDefault(duration));
                if (u is not null && LooksLikeUrl(u) && SourceDetector.Detect(u) != Models.SourcePlatform.Spotify)
                    result.Add(new BatchEntry { Url = u, Title = t, Artist = a, Album = al, DurationSeconds = dur, Display = t is null ? u : (a is null ? t : $"{a} - {t}") });
                else if (t is not null)
                    result.Add(new BatchEntry { Title = t, Artist = a, Album = al, DurationSeconds = dur, Display = a is null ? t : $"{a} - {t}" });
            }

            if (result.Count >= MaxEntries) break;
        }

        return Distinct(result);
    }

    private static BatchEntry FromText(string line)
    {
        if (LooksLikeUrl(line)) return new BatchEntry { Url = line, Display = line };
        if (TitleParser.TrySplitArtistTitle(line, out var a, out var t)) return new BatchEntry { Artist = a, Title = t, Display = line };
        return new BatchEntry { Title = line, Display = line };
    }

    private static string StripNumbering(string line)
    {
        var i = 0;
        while (i < line.Length && char.IsAsciiDigit(line[i])) i++;
        if (i is > 0 and <= 4 && i < line.Length - 2 && line[i] is '.' or ')' && line[i + 1] == ' ') return line[(i + 2)..].Trim();
        return line;
    }

    public static bool LooksLikeUrl(string s) =>
        (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
        !s.Contains(' ') && Uri.TryCreate(s, UriKind.Absolute, out _);

    private static double? ParseDuration(string? value, string? header)
    {
        if (value is null) return null;
        if (value.Contains(':'))
        {
            var parts = value.Split(':').Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
            if (parts.Any(double.IsNaN)) return null;
            return parts.Aggregate(0.0, (acc, p) => acc * 60 + p);
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n <= 0) return null;
        // Milliseconds when the header says so, or when the number is too large to be seconds.
        return header?.Contains("ms") == true || n > 24 * 3600 ? n / 1000.0 : n;
    }

    private static IReadOnlyList<BatchEntry> Distinct(List<BatchEntry> entries) =>
        entries.GroupBy(e => (e.Url ?? e.Query).ToLowerInvariant()).Select(g => g.First()).ToList();

    /// <summary>RFC 4180-ish CSV reader (quotes, escaped quotes, newlines in quotes). Detects ',' ';' or tab delimiters.</summary>
    internal static List<string[]> ReadCsv(string text)
    {
        text = text.Trim('﻿');
        var firstLine = text.Split(['\r', '\n'])[0];
        var delimiter = new[] { ',', ';', '\t' }.OrderByDescending(d => firstLine.Count(c => c == d)).First();

        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cell.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == delimiter) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString());
                cell.Clear();
                if (row.Any(v => v.Length > 0)) rows.Add([.. row]);
                row.Clear();
            }
            else cell.Append(c);
        }

        row.Add(cell.ToString());
        if (row.Any(v => v.Length > 0)) rows.Add([.. row]);
        return rows;
    }
}
