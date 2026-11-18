using System.Text.RegularExpressions;

namespace Sonic.Core.Helpers;

public static partial class VersionHelper
{
    [GeneratedRegex(@"\d+(?:\.\d+)+")]
    private static partial Regex VersionPattern();

    /// <summary>Extracts the first dotted version number (e.g. "2026.10.26" or "7.1.1") from text.</summary>
    public static string? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = VersionPattern().Match(text);
        return m.Success ? m.Value : null;
    }

    /// <summary>Compares dotted numeric versions. Returns &lt;0 when a &lt; b.</summary>
    public static int Compare(string? a, string? b)
    {
        var pa = Parse(a);
        var pb = Parse(b);
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }

        return 0;
    }

    private static long[] Parse(string? v)
    {
        var extracted = Extract(v);
        if (extracted is null) return [];
        return extracted.Split('.').Select(p => long.TryParse(p, out var n) ? n : 0).ToArray();
    }
}
