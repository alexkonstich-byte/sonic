using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;

namespace Sonic.Core.Services;

public sealed class YtDlpService(
    IDependencyService dependencies,
    IProcessRunner runner,
    ICookieService cookies,
    ISettingsService settings,
    ILogger<YtDlpService> logger) : IYtDlpService
{
    /// <summary>Base arguments: ignore user config files so behavior is predictable, force UTF-8 output.</summary>
    private IEnumerable<string> BaseArguments(CookieLease cookieLease)
    {
        yield return "--ignore-config";
        yield return "--encoding";
        yield return "utf-8";
        yield return "--color";
        yield return "never";
        yield return "--socket-timeout";
        yield return "30";
        foreach (var a in dependencies.GetYtDlpRuntimeArguments()) yield return a;
        // Optional: act as the user's own signed-in session (age checks, members-only content, bot checks).
        foreach (var a in cookieLease.Arguments) yield return a;
        foreach (var a in NetworkOptions.CommonArguments(settings.Current)) yield return a;
    }

    public async Task<string> GetStreamUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        var exe = dependencies.RequireYtDlp();
        using var cookieLease = cookies.Acquire();
        // AAC in M4A plays everywhere through Windows Media Foundation; fall back to any audio, then muxed.
        var args = BaseArguments(cookieLease)
            .Concat(["-g", "--no-playlist", "--no-warnings", "-f", "bestaudio[ext=m4a]/bestaudio[acodec^=mp4a]/bestaudio/best", "--", url])
            .ToList();
        var result = await runner.RunAsync(new ProcessRequest
        {
            FileName = exe,
            Arguments = args,
            CaptureStandardOutput = true,
            Timeout = TimeSpan.FromMinutes(2),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);

        var stream = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (result.ExitCode != 0 || stream is null || !stream.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var (message, retryable) = YtDlpOutputParser.Describe(result.StandardErrorTail);
            throw new YtDlpException(message, result.StandardErrorTail, retryable);
        }

        return stream;
    }

    public async Task<FeedPage> GetFeedAsync(string feed, int start, int count, CancellationToken cancellationToken = default)
    {
        var exe = dependencies.RequireYtDlp();
        using var cookieLease = cookies.Acquire();
        var end = start + count - 1;
        var args = BaseArguments(cookieLease)
            .Concat(["-J", "--flat-playlist", "--no-warnings", "--playlist-items", $"{start}:{end}", "--", feed])
            .ToList();
        var result = await runner.RunAsync(new ProcessRequest
        {
            FileName = exe,
            Arguments = args,
            CaptureStandardOutput = true,
            Timeout = TimeSpan.FromMinutes(3),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.TimedOut) throw new YtDlpException("YouTube took too long to answer. Try again.", null, true);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            var stderr = result.StandardErrorTail;
            var e = stderr.ToLowerInvariant();
            var needsLogin = e.Contains("logged in") || e.Contains("login required") || e.Contains("cookies")
                             || (e.Contains("sign in") && !e.Contains("not a bot"));
            if (needsLogin)
                throw new YtDlpException("YouTube needs you to be signed in. Import your browser cookies on this page or in Settings › Integrations › Cookies.", stderr, false);
            var (message, retryable) = YtDlpOutputParser.Describe(stderr);
            throw new YtDlpException(message, stderr, retryable);
        }

        using var doc = JsonDocument.Parse(result.StandardOutput);
        var info = ParseInfo(doc.RootElement, feed);
        var entries = info.IsPlaylist ? info.Entries : [info];
        return new FeedPage(entries, HasMore: entries.Count >= count);
    }

    public async Task<MediaInfo> AnalyzeAsync(string url, CancellationToken cancellationToken = default)
    {
        var exe = dependencies.RequireYtDlp();
        using var cookieLease = cookies.Acquire();
        var args = BaseArguments(cookieLease).Concat(["-J", "--flat-playlist", "--no-warnings", "--", url]).ToList();
        var result = await runner.RunAsync(new ProcessRequest
        {
            FileName = exe,
            Arguments = args,
            CaptureStandardOutput = true,
            Timeout = TimeSpan.FromMinutes(3),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.TimedOut) throw new YtDlpException("Analyzing the link took too long. Check your connection and try again.", null, true);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            var (message, retryable) = YtDlpOutputParser.Describe(result.StandardErrorTail);
            logger.LogWarning("Analyze failed for {Url}: {Error}", url, result.StandardErrorTail);
            throw new YtDlpException(message, result.StandardErrorTail, retryable);
        }

        using var doc = JsonDocument.Parse(result.StandardOutput);
        return ParseInfo(doc.RootElement, url);
    }

    public async Task<IReadOnlyList<MediaInfo>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default)
    {
        var exe = dependencies.RequireYtDlp();
        var count = Math.Clamp(maxResults, 1, 20);
        using var cookieLease = cookies.Acquire();
        var args = BaseArguments(cookieLease).Concat(["-J", "--flat-playlist", "--no-warnings", "--", $"ytsearch{count}:{query}"]).ToList();
        var result = await runner.RunAsync(new ProcessRequest
        {
            FileName = exe,
            Arguments = args,
            CaptureStandardOutput = true,
            Timeout = TimeSpan.FromMinutes(2),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            var (message, retryable) = YtDlpOutputParser.Describe(result.StandardErrorTail);
            throw new YtDlpException(message, result.StandardErrorTail, retryable);
        }

        using var doc = JsonDocument.Parse(result.StandardOutput);
        var info = ParseInfo(doc.RootElement, null);
        return info.IsPlaylist ? info.Entries : [info];
    }

    public async Task<YtDlpDownloadResult> DownloadAsync(
        YtDlpDownloadRequest request,
        IProgress<YtDlpProgress> progress,
        CancellationToken cancellationToken = default)
    {
        var exe = dependencies.RequireYtDlp();
        var ffmpeg = dependencies.RequireFfmpeg();
        Directory.CreateDirectory(request.WorkingDirectory);

        using var cookieLease = cookies.Acquire();
        var args = BaseArguments(cookieLease).ToList();
        args.AddRange(
        [
            "--no-playlist",
            "--newline",
            "--progress",
            "--progress-template", YtDlpOutputParser.ProgressTemplate,
            "--progress-template", YtDlpOutputParser.PostprocessTemplate,
            "--print", YtDlpOutputParser.FileTemplate,
            "-f", request.FormatSelector,
            "-P", request.WorkingDirectory,
            "-o", "media.%(ext)s",
            "--write-info-json",
            "--no-write-comments",
            "--ffmpeg-location", Path.GetDirectoryName(ffmpeg)!,
            "--continue",
            "--no-mtime",
            "--retries", "10",
            "--fragment-retries", "10",
        ]);
        if (request.WriteThumbnail) args.AddRange(["--write-thumbnail", "--convert-thumbnails", "jpg"]);
        args.AddRange(request.ExtraArguments);
        args.Add("--");
        args.Add(request.Url);

        string? reportedFile = null;
        // Progress lines can arrive on either stream depending on yt-dlp's quiet mode, so both are parsed.
        bool HandleStructured(string line)
        {
            if (YtDlpOutputParser.TryParseProgress(line, out var p))
            {
                progress.Report(p);
                return true;
            }

            if (YtDlpOutputParser.TryParsePostprocess(line, out var ppStatus, out var pp))
            {
                if (ppStatus == "started") progress.Report(new YtDlpProgress("postprocess", null, null, null, null, pp));
                return true;
            }

            return false;
        }

        var result = await runner.RunAsync(new ProcessRequest { FileName = exe, Arguments = args, WorkingDirectory = request.WorkingDirectory },
            onOutputLine: line =>
            {
                if (HandleStructured(line)) return;
                if (YtDlpOutputParser.TryParseFile(line, out var path)) reportedFile = path;
            },
            onErrorLine: line =>
            {
                if (HandleStructured(line)) return;
                if (line.StartsWith("ERROR", StringComparison.Ordinal)) logger.LogWarning("yt-dlp: {Line}", line);
                else if (line.StartsWith("WARNING", StringComparison.Ordinal)) logger.LogInformation("yt-dlp: {Line}", line);
            },
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            var (message, retryable) = YtDlpOutputParser.Describe(result.StandardErrorTail);
            throw new YtDlpException(message, result.StandardErrorTail, retryable);
        }

        var media = reportedFile is not null && File.Exists(reportedFile) ? reportedFile : FindMediaFile(request.WorkingDirectory);
        if (media is null) throw new YtDlpException("yt-dlp finished but no audio file was produced.", result.StandardErrorTail, true);

        var info = Directory.EnumerateFiles(request.WorkingDirectory, "*.info.json").FirstOrDefault();
        var thumb = Directory.EnumerateFiles(request.WorkingDirectory, "*.jpg").FirstOrDefault()
            ?? Directory.EnumerateFiles(request.WorkingDirectory, "*.webp").FirstOrDefault()
            ?? Directory.EnumerateFiles(request.WorkingDirectory, "*.png").FirstOrDefault();
        var subtitles = Directory.EnumerateFiles(request.WorkingDirectory, "media.*")
            .Where(f => YtDlpOutputParser.SubtitleExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();
        return new YtDlpDownloadResult(media, info, thumb, subtitles);
    }

    public async Task<MediaInfo?> ReadInfoJsonAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ParseInfo(doc.RootElement, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning(ex, "Could not read info json {Path}", path);
            return null;
        }
    }

    private static string? FindMediaFile(string dir)
    {
        string[] ignored = [".part", ".ytdl", ".json", ".jpg", ".webp", ".png", ".temp", .. YtDlpOutputParser.SubtitleExtensions];
        return Directory.EnumerateFiles(dir, "media.*")
            .Where(f => !ignored.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            // Unmerged parts look like media.f137.mp4; the final file has no format id.
            .Where(f => !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"^media\.f[\w-]+\."))
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
    }

    internal static MediaInfo ParseInfo(JsonElement root, string? requestedUrl)
    {
        var type = Str(root, "_type");
        var url = Str(root, "webpage_url") ?? Str(root, "original_url") ?? Str(root, "url") ?? requestedUrl ?? string.Empty;
        var extractor = Str(root, "extractor_key") ?? Str(root, "ie_key") ?? Str(root, "extractor");
        var isPlaylist = type is "playlist" or "multi_video";

        var entries = new List<MediaInfo>();
        if (isPlaylist && root.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in entriesEl.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var entry = ParseInfo(e, null);
                if (string.IsNullOrEmpty(entry.Url)) continue;
                entries.Add(entry);
            }
        }

        var formats = new List<MediaFormat>();
        var heights = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        if (root.TryGetProperty("formats", out var formatsEl) && formatsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in formatsEl.EnumerateArray())
            {
                var vcodecAny = Str(f, "vcodec");
                if (vcodecAny is not null and not "none" && Num(f, "height") is > 0 and var hh && Str(f, "format_note") != "storyboard")
                    heights.Add((int)hh);
                var acodec = Str(f, "acodec");
                if (acodec is null or "none") continue;
                var vcodec = Str(f, "vcodec");
                formats.Add(new MediaFormat
                {
                    FormatId = Str(f, "format_id") ?? string.Empty,
                    Extension = Str(f, "ext") ?? string.Empty,
                    AudioCodec = acodec,
                    AudioBitrateKbps = Num(f, "abr") ?? (vcodec is null or "none" ? Num(f, "tbr") : null),
                    SampleRate = (int?)Num(f, "asr"),
                    Channels = (int?)Num(f, "audio_channels"),
                    FileSize = (long?)(Num(f, "filesize") ?? Num(f, "filesize_approx")),
                    HasVideo = vcodec is not null and not "none",
                    Note = Str(f, "format_note"),
                });
            }
        }

        // Prefer audio-only formats; keep muxed formats only when nothing else exists.
        var audioOnly = formats.Where(f => !f.HasVideo).ToList();
        var shownFormats = (audioOnly.Count > 0 ? audioOnly : formats)
            .GroupBy(f => f.FormatId).Select(g => g.First())
            .OrderByDescending(f => f.AudioBitrateKbps ?? 0)
            .ToList();

        uint? year = (uint?)Num(root, "release_year");
        if (year is null && Str(root, "release_date") is { Length: >= 4 } rd && uint.TryParse(rd[..4], out var y)) year = y;
        if (year is null && Str(root, "upload_date") is { Length: >= 4 } ud && uint.TryParse(ud[..4], out var uy) && Str(root, "track") is not null) year = uy;

        string? genre = Str(root, "genre");
        if (genre is null && root.TryGetProperty("genres", out var g2) && g2.ValueKind == JsonValueKind.Array)
            genre = g2.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

        string? artist = Str(root, "artist") ?? Str(root, "creator");
        if (artist is null && root.TryGetProperty("artists", out var artistsEl) && artistsEl.ValueKind == JsonValueKind.Array)
            artist = string.Join(", ", artistsEl.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(artist)) artist = null;

        return new MediaInfo
        {
            Id = Str(root, "id") ?? string.Empty,
            Url = url,
            Title = Str(root, "title") ?? Str(root, "id") ?? "Untitled",
            Uploader = TitleParser.CleanUploader(Str(root, "uploader") ?? Str(root, "channel") ?? Str(root, "playlist_uploader")),
            Artist = artist,
            Track = Str(root, "track"),
            Album = Str(root, "album"),
            Genre = genre,
            ReleaseYear = year,
            TrackNumber = (uint?)Num(root, "track_number"),
            ThumbnailUrl = PickThumbnail(root),
            DurationSeconds = Num(root, "duration"),
            Extractor = extractor,
            Source = SourceDetector.FromExtractor(extractor, url),
            IsPlaylist = isPlaylist,
            IsLive = root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True,
            AudioFormats = shownFormats,
            Entries = entries,
            UploadDate = Str(root, "upload_date"),
            Height = (int?)Num(root, "height"),
            VideoHeights = heights.ToList(),
            Subtitles = ParseSubtitles(root, Str(root, "language")),
            Language = Str(root, "language"),
        };
    }

    /// <summary>
    /// Manual subtitles are all offered. Automatic captions on YouTube include ~150 machine translations, so only
    /// the original-language track (and the video's own language) are listed.
    /// </summary>
    private static List<SubtitleTrack> ParseSubtitles(JsonElement root, string? language)
    {
        var list = new List<SubtitleTrack>();
        if (root.TryGetProperty("subtitles", out var subs) && subs.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in subs.EnumerateObject())
            {
                if (p.Name is "live_chat" or "rechat") continue;
                list.Add(new SubtitleTrack(p.Name, SubtitleName(p.Value, p.Name), false));
            }
        }

        if (root.TryGetProperty("automatic_captions", out var auto) && auto.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in auto.EnumerateObject())
            {
                var isOriginal = p.Name.EndsWith("-orig", StringComparison.Ordinal);
                var isVideoLanguage = language is not null && string.Equals(p.Name, language, StringComparison.OrdinalIgnoreCase);
                if (!isOriginal && !isVideoLanguage) continue;
                if (list.Any(s => s.Code == p.Name)) continue;
                list.Add(new SubtitleTrack(p.Name, SubtitleName(p.Value, p.Name), true));
            }
        }

        return list;
    }

    private static string SubtitleName(JsonElement tracks, string code)
    {
        if (tracks.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tracks.EnumerateArray())
            {
                if (Str(t, "name") is { } name && !string.Equals(name, code, StringComparison.OrdinalIgnoreCase)) return name;
            }
        }

        return LanguageName(code);
    }

    /// <summary>Turns a code such as "pt-br" or "en-orig" into "Portuguese (Brazil)" / "English (original)".</summary>
    internal static string LanguageName(string code)
    {
        var original = code.EndsWith("-orig", StringComparison.Ordinal);
        var lookup = original ? code[..^5] : code;
        try
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(lookup);
            if (!culture.EnglishName.StartsWith("Unknown", StringComparison.Ordinal) && culture.ThreeLetterISOLanguageName != "ivl")
                return original ? $"{culture.EnglishName} (original)" : culture.EnglishName;
        }
        catch (System.Globalization.CultureNotFoundException)
        {
        }

        return code;
    }

    private static string? PickThumbnail(JsonElement root)
    {
        if (Str(root, "thumbnail") is { } t) return t;
        if (root.TryGetProperty("thumbnails", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            string? best = null;
            double bestScore = -1;
            foreach (var th in arr.EnumerateArray())
            {
                var u = Str(th, "url");
                if (u is null) continue;
                var score = (Num(th, "width") ?? 0) * (Num(th, "height") ?? 0) + (Num(th, "preference") ?? 0);
                if (score >= bestScore) { bestScore = score; best = u; }
            }

            return best;
        }

        return null;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static double? Num(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}
