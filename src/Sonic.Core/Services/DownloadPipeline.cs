using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;

namespace Sonic.Core.Services;

/// <summary>Download → convert → tag → move → import, for a single item.</summary>
public sealed class DownloadPipeline(
    IYtDlpService ytDlp,
    IFfmpegService ffmpeg,
    IMetadataService metadata,
    ILibraryService library,
    IPlaylistService playlists,
    ISettingsService settings,
    IAppPaths paths,
    IHttpClientFactory httpFactory,
    ICoverArtService coverArt,
    ILogger<DownloadPipeline> logger) : IDownloadPipeline
{
    private string GetWorkDirectory(Guid id) => Path.Combine(paths.TempDirectory, id.ToString("N"));

    public void DeleteWorkingData(Guid id)
    {
        var dir = GetWorkDirectory(id);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete working folder {Dir}", dir);
        }
    }

    public async Task ExecuteAsync(DownloadItem item, Action<DownloadItem> onProgress, CancellationToken cancellationToken)
    {
        var s = settings.Current;
        var work = GetWorkDirectory(item.Id);
        Directory.CreateDirectory(work);
        EnsureOutputDirectory(item.OutputDirectory);

        if (item.Kind == MediaKind.Video)
        {
            await ExecuteVideoAsync(item, work, onProgress, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Skip early when we already know the artist/title and the library has it.
        if (s.DuplicateHandling == DuplicateHandling.Skip && await TrySkipDuplicateAsync(item, item.Hints?.Artist ?? item.Artist, item.Hints?.Title, cancellationToken).ConfigureAwait(false))
            return;

        // 1. Download
        SetStage(item, DownloadStatus.Downloading, "Downloading", onProgress);
        var result = await ytDlp.DownloadAsync(new YtDlpDownloadRequest
        {
            Url = item.Url,
            WorkingDirectory = work,
            FormatSelector = BuildFormatSelector(item),
            WriteThumbnail = s.EmbedArtwork,
            ExtraArguments = NetworkOptions.DownloadArguments(s, MediaKind.Audio).ToList(),
        }, new SyncProgress<YtDlpProgress>(p =>
        {
            if (p.Status == "postprocess")
            {
                // e.g. SponsorBlock cutting non-music parts before Sonic converts the file.
                item.StageText = YtDlpOutputParser.DescribePostprocessor(p.Postprocessor);
            }
            else if (p.Status == "finished")
            {
                item.Progress = 100;
            }
            else
            {
                item.DownloadedBytes = p.DownloadedBytes ?? item.DownloadedBytes;
                item.TotalBytes = p.TotalBytes ?? item.TotalBytes;
                if (item.TotalBytes is > 0) item.Progress = Math.Clamp(item.DownloadedBytes * 100.0 / item.TotalBytes.Value, 0, 100);
            }

            item.SpeedBytesPerSecond = p.SpeedBytesPerSecond;
            item.EtaSeconds = p.EtaSeconds;
            onProgress(item);
        }), cancellationToken).ConfigureAwait(false);

        item.SpeedBytesPerSecond = null;
        item.EtaSeconds = null;
        if (File.Exists(result.MediaFilePath)) item.TotalBytes = new FileInfo(result.MediaFilePath).Length;

        var info = result.InfoJsonPath is not null ? await ytDlp.ReadInfoJsonAsync(result.InfoJsonPath, cancellationToken).ConfigureAwait(false) : null;
        var tags = BuildMetadata(item, info, s.CleanTitles);
        item.Title = tags.Title;
        item.Artist = tags.Artist;
        item.ThumbnailUrl ??= info?.ThumbnailUrl;
        item.DurationSeconds ??= info?.DurationSeconds;

        if (s.DuplicateHandling == DuplicateHandling.Skip && await TrySkipDuplicateAsync(item, tags.Artist, tags.Title, cancellationToken).ConfigureAwait(false))
        {
            DeleteWorkingData(item.Id);
            return;
        }

        // 2. Convert (or losslessly remux)
        SetStage(item, DownloadStatus.Converting, "Converting", onProgress);
        var converted = await ffmpeg.ConvertAudioAsync(result.MediaFilePath, Path.Combine(work, "output"), item.Format, item.Quality,
            new SyncProgress<double>(p =>
            {
                item.Progress = p;
                onProgress(item);
            }), cancellationToken).ConfigureAwait(false);

        // 3. Tag
        SetStage(item, DownloadStatus.Tagging, "Writing tags", onProgress);
        if (s.EmbedArtwork) await AttachArtworkAsync(item, result.ThumbnailPath, tags, work, s.CropArtworkToSquare, s.FindAlbumArt, cancellationToken).ConfigureAwait(false);
        tags.Comment = item.Url;
        await Task.Run(() =>
        {
            // Tags copied from the source by FFmpeg are kept; known values replace them.
            var existing = metadata.Read(converted, includeArtwork: false);
            if (!string.IsNullOrWhiteSpace(existing.Comment)) tags.Comment = null;
            metadata.Write(converted, tags, overwriteAll: false);
        }, cancellationToken).ConfigureAwait(false);

        // 4. Move into the library folder without overwriting anything.
        cancellationToken.ThrowIfCancellationRequested();
        var relative = FileNameHelper.ExpandTemplate(item.FilenameTemplate, tags, info?.Uploader, info?.Id) + Path.GetExtension(converted);
        var destination = MoveToDestination(converted, Path.Combine(item.OutputDirectory, relative));
        item.FilePath = destination;
        item.TotalBytes = new FileInfo(destination).Length;
        logger.LogInformation("Saved {File}", destination);

        // 5. Add to the library and link to a playlist entry if requested.
        try
        {
            var track = await library.ImportFileAsync(destination, item.Url, CancellationToken.None).ConfigureAwait(false);
            if (track is not null && item.PlaylistEntryId is { } entryId)
                await playlists.LinkEntryAsync(entryId, track.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The file is saved; a library refresh can pick it up later.
            logger.LogError(ex, "Downloaded file could not be added to the library");
        }

        item.Progress = 100;
        DeleteWorkingData(item.Id);
    }

    /// <summary>Video: yt-dlp selects streams, merges them with FFmpeg and embeds subtitles/thumbnail/chapters.</summary>
    private async Task ExecuteVideoAsync(DownloadItem item, string work, Action<DownloadItem> onProgress, CancellationToken cancellationToken)
    {
        var options = item.Video ?? settings.Current.CreateDefaultVideoOptions();
        SetStage(item, DownloadStatus.Downloading, "Downloading", onProgress);

        var result = await ytDlp.DownloadAsync(new YtDlpDownloadRequest
        {
            Url = item.Url,
            WorkingDirectory = work,
            FormatSelector = BuildVideoFormatSelector(options),
            WriteThumbnail = false,
            ExtraArguments = [.. BuildVideoArguments(options), .. NetworkOptions.DownloadArguments(settings.Current, MediaKind.Video)],
        }, new SyncProgress<YtDlpProgress>(p =>
        {
            switch (p.Status)
            {
                case "postprocess":
                    item.Status = DownloadStatus.Converting;
                    item.StageText = YtDlpOutputParser.DescribePostprocessor(p.Postprocessor);
                    item.Progress = 0;
                    item.SpeedBytesPerSecond = null;
                    item.EtaSeconds = null;
                    break;
                case "finished":
                    // Video, audio and subtitles arrive as separate streams; each reports its own progress.
                    item.Progress = 100;
                    break;
                default:
                    item.Status = DownloadStatus.Downloading;
                    item.StageText = p.Stream switch
                    {
                        "video" => "Downloading video",
                        "audio" => "Downloading audio",
                        "subtitles" => "Downloading subtitles",
                        _ => "Downloading",
                    };
                    item.DownloadedBytes = p.DownloadedBytes ?? item.DownloadedBytes;
                    item.TotalBytes = p.TotalBytes ?? item.TotalBytes;
                    if (p.TotalBytes is > 0 && p.DownloadedBytes is { } done) item.Progress = Math.Clamp(done * 100.0 / p.TotalBytes.Value, 0, 100);
                    item.SpeedBytesPerSecond = p.SpeedBytesPerSecond;
                    item.EtaSeconds = p.EtaSeconds;
                    break;
            }

            onProgress(item);
        }), cancellationToken).ConfigureAwait(false);

        item.SpeedBytesPerSecond = null;
        item.EtaSeconds = null;
        var info = result.InfoJsonPath is not null ? await ytDlp.ReadInfoJsonAsync(result.InfoJsonPath, cancellationToken).ConfigureAwait(false) : null;
        item.Title = info?.Title ?? item.Title;
        item.Artist = info?.Uploader ?? item.Artist;
        item.ThumbnailUrl ??= info?.ThumbnailUrl;
        item.DurationSeconds ??= info?.DurationSeconds;

        cancellationToken.ThrowIfCancellationRequested();
        SetStage(item, DownloadStatus.Tagging, "Saving", onProgress);
        var relative = FileNameHelper.ExpandVideoTemplate(item.FilenameTemplate, info?.Title ?? item.Title, info?.Uploader ?? item.Artist,
            info?.Id, info?.UploadDate, info?.Height);
        var destination = MoveToDestination(result.MediaFilePath, Path.Combine(item.OutputDirectory, relative + Path.GetExtension(result.MediaFilePath)));

        // Separate subtitle files keep the video's name: "Video.en.srt". When embedding, yt-dlp leaves the
        // converted files behind; they're already inside the video and are discarded with the working folder.
        var baseName = Path.Combine(Path.GetDirectoryName(destination)!, Path.GetFileNameWithoutExtension(destination));
        var savedSubtitles = 0;
        if (options.SubtitleMode == SubtitleMode.Separate)
        {
            foreach (var subtitle in result.SubtitlePaths)
            {
                var suffix = Path.GetFileName(subtitle)["media".Length..];
                MoveToDestination(subtitle, baseName + suffix);
                savedSubtitles++;
            }
        }

        item.FilePath = destination;
        item.TotalBytes = new FileInfo(destination).Length;
        item.Progress = 100;
        logger.LogInformation("Saved video {File} ({Mode} subtitles, {Count} files found, {Saved} saved separately)",
            destination, options.SubtitleMode, result.SubtitlePaths.Count, savedSubtitles);
        DeleteWorkingData(item.Id);
    }

    internal static string BuildVideoFormatSelector(VideoOptions v)
    {
        var h = v.MaxHeight > 0 ? $"[height<={v.MaxHeight}]" : string.Empty;
        var preferred = (v.Container, v.Codec) switch
        {
            // H.264 + AAC in MP4: the combination every player, TV and editor understands.
            (VideoContainer.Mp4, VideoCodecPreference.Compatible) => $"bv*[vcodec^=avc1]{h}+ba[ext=m4a]/b[ext=mp4]{h}/",
            (VideoContainer.Webm, _) => $"bv*[ext=webm]{h}+ba[ext=webm]/",
            (_, VideoCodecPreference.Compatible) => $"bv*[vcodec^=avc1]{h}+ba/",
            _ => string.Empty,
        };
        // Fall back to the best streams within the height limit, then to anything at all.
        return $"{preferred}bv*{h}+ba/b{h}/bv*+ba/b";
    }

    internal static List<string> BuildVideoArguments(VideoOptions v)
    {
        var container = v.Container switch
        {
            VideoContainer.Mkv => "mkv",
            VideoContainer.Webm => "webm",
            _ => "mp4",
        };
        var args = new List<string> { "--merge-output-format", container == "webm" ? "webm/mkv" : container };
        if (container != "webm") args.AddRange(["--remux-video", container]);
        if (v.EmbedMetadata) args.Add("--embed-metadata");
        if (v.EmbedChapters) args.Add("--embed-chapters");
        // WebM can't carry cover art.
        if (v.EmbedThumbnail && container != "webm") args.Add("--embed-thumbnail");

        var languages = string.Join(',', (v.SubtitleLanguages ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (v.SubtitleMode != SubtitleMode.None && languages.Length > 0)
        {
            var format = v.SubtitleFormat;
            if (v.SubtitleMode == SubtitleMode.Embed)
            {
                // Containers only accept certain subtitle codecs: WebM → WebVTT, MP4 → mov_text (from SRT/VTT).
                if (container == "webm") format = SubtitleFormat.Vtt;
                else if (container == "mp4" && format == SubtitleFormat.Ass) format = SubtitleFormat.Srt;
            }

            var ext = format switch
            {
                SubtitleFormat.Vtt => "vtt",
                SubtitleFormat.Ass => "ass",
                _ => "srt",
            };
            args.Add("--write-subs");
            if (v.IncludeAutoSubtitles) args.Add("--write-auto-subs");
            args.AddRange(["--sub-langs", languages, "--sub-format", $"{ext}/best", "--convert-subs", ext]);
            if (v.SubtitleMode == SubtitleMode.Embed) args.Add("--embed-subs");
        }

        return args;
    }

    private async Task<bool> TrySkipDuplicateAsync(DownloadItem item, string? artist, string? title, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return false;
        var existing = await library.FindByKeyAsync(artist, title, ct).ConfigureAwait(false);
        if (existing is null) return false;

        logger.LogInformation("Skipping {Artist} - {Title}: already in library at {Path}", artist, title, existing.FilePath);
        item.Status = DownloadStatus.Skipped;
        item.FilePath = existing.FilePath;
        item.ErrorMessage = "Already in your library";
        item.Progress = 100;
        if (item.PlaylistEntryId is { } entryId) await playlists.LinkEntryAsync(entryId, existing.Id, ct).ConfigureAwait(false);
        return true;
    }

    internal static string BuildFormatSelector(DownloadItem item)
    {
        var preferred = item.Format switch
        {
            AudioFormat.M4a => "bestaudio[ext=m4a]/",
            AudioFormat.Opus or AudioFormat.Ogg => "bestaudio[acodec=opus]/",
            AudioFormat.Mp3 => "bestaudio[acodec=mp3]/",
            _ => string.Empty,
        };
        var specific = string.IsNullOrWhiteSpace(item.FormatId) ? string.Empty : item.FormatId + "/";
        return specific + preferred + "bestaudio/best";
    }

    internal static TrackMetadata BuildMetadata(DownloadItem item, MediaInfo? info, bool cleanTitles)
    {
        var hints = item.Hints;
        var title = hints?.Title;
        var artist = hints?.Artist;

        if (info is not null)
        {
            title ??= info.Track;
            artist ??= info.Artist;
            if (title is null)
            {
                if (artist is null && TitleParser.TrySplitArtistTitle(info.Title, out var a, out var t))
                {
                    artist = a;
                    title = t;
                }
                else
                {
                    title = cleanTitles ? TitleParser.Clean(info.Title) : info.Title;
                }
            }

            artist ??= info.Uploader;
        }
        else
        {
            if (title is null && item.Title is not null)
            {
                if (artist is null && TitleParser.TrySplitArtistTitle(item.Title, out var a, out var t))
                {
                    artist = a;
                    title = t;
                }
                else
                {
                    title = cleanTitles ? TitleParser.Clean(item.Title) : item.Title;
                }
            }

            artist ??= item.Artist;
        }

        return new TrackMetadata
        {
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            Artist = string.IsNullOrWhiteSpace(artist) ? null : TitleParser.CleanUploader(artist)?.Trim(),
            Album = hints?.Album ?? info?.Album,
            AlbumArtist = hints?.AlbumArtist,
            TrackNumber = hints?.TrackNumber ?? info?.TrackNumber,
            Year = hints?.Year ?? info?.ReleaseYear,
            Genre = hints?.Genre ?? info?.Genre,
        };
    }

    private async Task AttachArtworkAsync(DownloadItem item, string? thumbnailPath, TrackMetadata tags, string work, bool crop, bool findAlbumArt, CancellationToken ct)
    {
        try
        {
            string? source = null;
            var isAlbumArt = false;
            if (item.Hints?.ArtworkUrl is { } artUrl)
            {
                source = Path.Combine(work, "hint-artwork.img");
                var http = httpFactory.CreateClient(DependencyService.HttpClientName);
                var bytes = await http.GetByteArrayAsync(artUrl, ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(source, bytes, ct).ConfigureAwait(false);
                isAlbumArt = true;
            }
            else if (findAlbumArt && await FindAlbumArtAsync(item, tags, work, ct).ConfigureAwait(false) is { } found)
            {
                source = found;
                isAlbumArt = true;
            }
            else if (thumbnailPath is not null && File.Exists(thumbnailPath))
            {
                source = thumbnailPath;
            }

            if (source is null) return;
            var cover = await ffmpeg.PrepareArtworkAsync(source, Path.Combine(work, "cover.jpg"), crop && !isAlbumArt, ct).ConfigureAwait(false);
            tags.Artwork = await File.ReadAllBytesAsync(cover, ct).ConfigureAwait(false);
            tags.ArtworkMimeType = "image/jpeg";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Missing artwork should not fail the whole download.
            logger.LogWarning(ex, "Could not prepare artwork for {Url}", item.Url);
        }
    }

    /// <summary>
    /// Replaces the video thumbnail with the real album cover when a catalog match is confident.
    /// Also fills an empty album and year from the same release.
    /// </summary>
    private async Task<string?> FindAlbumArtAsync(DownloadItem item, TrackMetadata tags, string work, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tags.Title) || string.IsNullOrWhiteSpace(tags.Artist)) return null;
        try
        {
            var candidates = await coverArt.SearchAsync(tags.Artist, tags.Title!, item.DurationSeconds, ct).ConfigureAwait(false);
            if (candidates.FirstOrDefault() is not { Score: >= CoverArtService.ConfidentScore } best) return null;
            var bytes = await coverArt.DownloadAsync(best, ct).ConfigureAwait(false);
            if (bytes.Length == 0) return null;
            var path = Path.Combine(work, "album-art.img");
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            tags.Album ??= best.Album;
            tags.Year ??= best.Year is > 0 ? (uint)best.Year.Value : null;
            logger.LogInformation("Album art for {Title}: {Album} ({Source}, score {Score:0.00})", tags.Title, best.Album, best.Source, best.Score);
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Album art lookup failed for {Url}", item.Url);
            return null;
        }
    }

    private static void SetStage(DownloadItem item, DownloadStatus status, string text, Action<DownloadItem> onProgress)
    {
        item.Status = status;
        item.StageText = text;
        item.Progress = 0;
        onProgress(item);
    }

    private static void EnsureOutputDirectory(string dir)
    {
        if (FileNameHelper.ValidateDirectory(dir) is { } error) throw new InvalidOperationException($"Invalid download folder: {error}");
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Sonic doesn't have permission to write to \"{dir}\". Choose another download folder in Settings.");
        }
    }

    private static string MoveToDestination(string source, string desired)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(desired)!);
        for (var attempt = 0; ; attempt++)
        {
            var target = FileNameHelper.GetUniquePath(desired);
            try
            {
                File.Move(source, target, overwrite: false);
                return target;
            }
            catch (IOException) when (File.Exists(target) && attempt < 5)
            {
                // Another download claimed the same name between the check and the move; pick the next one.
            }
        }
    }
}
