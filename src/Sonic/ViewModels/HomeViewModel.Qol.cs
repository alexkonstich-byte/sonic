using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;
using Sonic.Core.Services;
using Sonic.Helpers;
using Sonic.Services;

namespace Sonic.ViewModels;

/// <summary>A YouTube search result on the Home page.</summary>
public sealed partial class SearchResultItem(MediaInfo info) : ObservableObject
{
    public MediaInfo Info { get; } = info;
    public string Title => Info.Title;
    public string Subtitle => string.Join(" · ", new[] { Info.Uploader ?? Info.DisplayArtist, Info.DurationSeconds is > 0 ? Format.Duration(Info.DurationSeconds) : null }.Where(s => !string.IsNullOrEmpty(s)));
    public string? Thumbnail => Info.ThumbnailUrl ?? (Info.Id is { Length: > 0 } id ? $"https://i.ytimg.com/vi/{id}/mqdefault.jpg" : null);

    [ObservableProperty] private bool _isDownloaded;
}

/// <summary>Quick download, presets, search, "already downloaded" and preview.</summary>
public sealed partial class HomeViewModel
{
    private CancellationTokenSource? _searchCts;
    private bool _applyingPreset;

    // ---------------------------------------------------------------- Search

    public ObservableCollection<SearchResultItem> SearchResults { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchResults))]
    private bool _isSearching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchResults))]
    private string _searchTitle = string.Empty;

    public bool HasSearchResults => SearchResults.Count > 0 && !IsSearching;

    /// <summary>Text that isn't a link is treated as a YouTube search.</summary>
    public bool IsSearchQuery
    {
        get
        {
            var text = Url.Trim();
            if (text.Length == 0 || text.Contains("://", StringComparison.Ordinal) || text.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase)) return false;
            return text.Contains(' ') || !SourceDetector.TryNormalizeUrl(text, out _);
        }
    }

    /// <summary>Quick download only makes sense for real links.</summary>
    public bool CanQuickDownload => HasUrl && !IsSearchQuery && !IsSpotifyUrl;

    private async Task SearchAsync()
    {
        var query = Url.Trim();
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        IsSearching = true;
        AnalysisError = null;
        Analysis = null;
        SearchResults.Clear();
        SearchTitle = $"Results for \"{query}\"";
        try
        {
            var results = await Task.Run(() => _ytDlp.SearchAsync(query, 15, token), token);
            if (token.IsCancellationRequested) return;
            foreach (var r in results.Where(r => !string.IsNullOrWhiteSpace(r.Url))) SearchResults.Add(new SearchResultItem(r));
            if (SearchResults.Count == 0) AnalysisError = $"Nothing found for \"{query}\".";
            _ = MarkDownloadedAsync(SearchResults.ToList(), token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (DependencyMissingException ex)
        {
            AnalysisError = ex.Message;
        }
        catch (YtDlpException ex)
        {
            AnalysisError = ex.Message;
            App.GetService<IZapretAdvisor>().MaybeSuggest(ex.Message, "https://www.youtube.com/");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed");
            AnalysisError = "Search failed: " + ex.Message;
        }
        finally
        {
            IsSearching = false;
            OnPropertyChanged(nameof(HasSearchResults));
        }
    }

    private async Task MarkDownloadedAsync(IReadOnlyList<SearchResultItem> items, CancellationToken token)
    {
        if (!_settings.Current.WarnAlreadyDownloaded) return;
        foreach (var item in items)
        {
            if (token.IsCancellationRequested) return;
            try
            {
                if (await _queue.FindPreviousDownloadAsync(item.Info.Url, token) is not null) item.IsDownloaded = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "History lookup failed");
            }
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        _searchCts?.Cancel();
        SearchResults.Clear();
        SearchTitle = string.Empty;
        IsSearching = false;
        OnPropertyChanged(nameof(HasSearchResults));
    }

    [RelayCommand]
    private async Task OpenSearchResultAsync(SearchResultItem item)
    {
        ClearSearch();
        Url = item.Info.Url;
        await AnalyzeAsync();
    }

    [RelayCommand]
    private Task DownloadResultAudioAsync(SearchResultItem item) => QuickEnqueueAsync(item.Info.Url, MediaKind.Audio, item.Info);

    [RelayCommand]
    private Task DownloadResultVideoAsync(SearchResultItem item) => QuickEnqueueAsync(item.Info.Url, MediaKind.Video, item.Info);

    [RelayCommand]
    private Task PreviewResultAsync(SearchResultItem item) => App.GetService<PlayerViewModel>().PreviewAsync(item.Info);

    [RelayCommand]
    private Task PreviewAnalysisAsync() =>
        Analysis is { IsPlaylist: false } info ? App.GetService<PlayerViewModel>().PreviewAsync(info) : Task.CompletedTask;

    [RelayCommand]
    private Task PreviewPlaylistItemAsync(PlaylistItemChoice item) => App.GetService<PlayerViewModel>().PreviewAsync(item.Info);

    // ---------------------------------------------------------------- Quick download

    /// <summary>Queues the link with the current options, skipping analysis.</summary>
    [RelayCommand]
    private async Task QuickDownloadAsync()
    {
        if (!SourceDetector.TryNormalizeUrl(Url, out var uri))
        {
            AnalysisError = "That doesn't look like a valid link.";
            return;
        }

        if (IsSpotifyUrl)
        {
            await AnalyzeAsync();
            return;
        }

        if (await QuickEnqueueAsync(uri.AbsoluteUri, IsVideoMode ? MediaKind.Video : MediaKind.Audio, null)) ClearAnalysis();
    }

    private async Task<bool> QuickEnqueueAsync(string url, MediaKind kind, MediaInfo? info)
    {
        if (SourceDetector.IsProtectedStreamingService(url))
        {
            AnalysisError = "This is a DRM-protected streaming service, so its audio can't be downloaded.";
            return false;
        }

        var sameKind = kind == (IsVideoMode ? MediaKind.Video : MediaKind.Audio);
        var template = sameKind ? FilenameTemplate : kind == MediaKind.Video ? _settings.Current.VideoFilenameTemplate : _settings.Current.FilenameTemplate;
        if (FileNameHelper.ValidateTemplate(template, kind) is { } templateError)
        {
            await _dialogs.ShowErrorAsync("Check the file name", templateError);
            return false;
        }

        var folder = sameKind ? SaveToFolder.Trim() : kind == MediaKind.Video ? _settings.Current.VideoDirectory : _settings.Current.DownloadDirectory;
        if (FileNameHelper.ValidateDirectory(folder) is { } folderError)
        {
            await _dialogs.ShowErrorAsync("Check the folder", folderError);
            return false;
        }

        if (!await ConfirmIfDownloadedAsync(url)) return false;

        var video = kind != MediaKind.Video ? null : sameKind ? BuildVideoOptions() : new VideoOptions
        {
            Container = _settings.Current.DefaultVideoContainer,
            MaxHeight = _settings.Current.DefaultVideoMaxHeight,
            Codec = _settings.Current.DefaultVideoCodec,
            SubtitleMode = _settings.Current.DefaultSubtitleMode,
            SubtitleFormat = _settings.Current.DefaultSubtitleFormat,
            SubtitleLanguages = _settings.Current.DefaultSubtitleLanguages,
            IncludeAutoSubtitles = _settings.Current.IncludeAutoSubtitles,
            EmbedThumbnail = _settings.Current.EmbedVideoThumbnail,
            EmbedChapters = _settings.Current.EmbedVideoChapters,
        };

        try
        {
            await _queue.EnqueueAsync(new DownloadRequest
            {
                Url = url,
                Kind = kind,
                Video = video,
                FilenameTemplate = template,
                OutputDirectory = folder,
                Title = info?.Title,
                Artist = info is null ? null : info.Artist ?? info.Uploader,
                ThumbnailUrl = info?.ThumbnailUrl,
                DurationSeconds = info?.DurationSeconds,
                Format = sameKind ? SelectedFormat.Value : _settings.Current.DefaultFormat,
                Quality = sameKind ? SelectedQuality.Value : _settings.Current.DefaultQuality,
            });
            _notifications.ShowSuccess("Added to downloads", info?.Title ?? url);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await _dialogs.ShowErrorAsync("Couldn't add the download", ex.Message);
            return false;
        }
    }

    // ---------------------------------------------------------------- Already downloaded

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviousDownload), nameof(PreviousDownloadText), nameof(PreviousFileExists))]
    private DownloadItem? _previousDownload;

    public bool HasPreviousDownload => PreviousDownload is not null;
    public bool PreviousFileExists => PreviousDownload?.FilePath is { } p && (File.Exists(p) || Directory.Exists(p));

    public string PreviousDownloadText => PreviousDownload is not { } p ? string.Empty
        : $"Downloaded {Format.RelativeTime(p.CompletedAt ?? p.CreatedAt)} as {(p.Kind == MediaKind.Video ? "video" : "audio")}"
          + (p.FilePath is { } path ? $": {Path.GetFileName(path)}" : ".")
          + (PreviousFileExists ? string.Empty : " (the file is no longer there)");

    private async Task CheckPreviousDownloadAsync(MediaInfo info)
    {
        PreviousDownload = null;
        if (!_settings.Current.WarnAlreadyDownloaded || info.IsPlaylist) return;
        try
        {
            PreviousDownload = await _queue.FindPreviousDownloadAsync(info.Url);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "History lookup failed");
        }
    }

    private async Task<bool> ConfirmIfDownloadedAsync(string url)
    {
        if (!_settings.Current.WarnAlreadyDownloaded) return true;
        DownloadItem? previous;
        try
        {
            previous = await _queue.FindPreviousDownloadAsync(url);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "History lookup failed");
            return true;
        }

        if (previous?.FilePath is not { } file || !File.Exists(file)) return true;
        return await _dialogs.ConfirmAsync("Already downloaded",
            $"You downloaded this {Format.RelativeTime(previous.CompletedAt ?? previous.CreatedAt)}:\n{file}\n\nDownload it again?",
            "Download again", closeText: "Skip");
    }

    [RelayCommand]
    private void OpenPreviousFile()
    {
        if (PreviousDownload?.FilePath is { } p) _launcher.OpenFile(p);
    }

    [RelayCommand]
    private void RevealPreviousFile()
    {
        if (PreviousDownload?.FilePath is { } p) _launcher.RevealInExplorer(p);
    }

    // ---------------------------------------------------------------- Presets

    public ObservableCollection<DownloadPreset> Presets { get; } = [];

    [ObservableProperty] private DownloadPreset? _selectedPreset;

    private void LoadPresets()
    {
        _applyingPreset = true;
        try
        {
            SelectedPreset = null;
            Presets.Clear();
            foreach (var p in _settings.Current.Presets) Presets.Add(p);
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    partial void OnSelectedPresetChanged(DownloadPreset? value)
    {
        if (_applyingPreset || value is null) return;
        ApplyPreset(value);
        _notifications.ShowInfo("Preset applied", value.Name);
        // The box works as an "apply" menu: reset it so the same preset can be picked again.
        _dispatcher.Post(() =>
        {
            _applyingPreset = true;
            SelectedPreset = null;
            _applyingPreset = false;
        });
    }

    private void ApplyPreset(DownloadPreset preset)
    {
        _applyingPreset = true;
        try
        {
            ModeIndex = preset.Kind == MediaKind.Video ? 1 : 0;
            if (preset.Kind == MediaKind.Audio)
            {
                SelectedFormat = Options.Find(Options.Formats, preset.Format);
                SelectedQuality = Options.Find(Options.Qualities, preset.Quality);
            }
            else if (preset.Video is { } v)
            {
                SelectedContainer = Options.Find(Options.Containers, v.Container);
                SelectedVideoQuality = Options.PickHeight(VideoQualityOptions, v.MaxHeight);
                SelectedVideoCodec = Options.Find(Options.VideoCodecs, v.Codec);
                SelectedSubtitleMode = Options.Find(Options.SubtitleModes, v.SubtitleMode);
                SelectedSubtitleFormat = Options.Find(Options.SubtitleFormats, v.SubtitleFormat);
                SubtitleLanguagesText = v.SubtitleLanguages ?? string.Empty;
                IncludeAutoSubtitles = v.IncludeAutoSubtitles;
                EmbedThumbnail = v.EmbedThumbnail;
                EmbedChapters = v.EmbedChapters;
                if (Analysis is { IsPlaylist: false } info) BuildVideoChoices(info);
            }

            FilenameTemplate = preset.FilenameTemplate ?? FileNameHelper.DefaultTemplateFor(preset.Kind);
            if (!string.IsNullOrWhiteSpace(preset.OutputDirectory)) SaveToFolder = preset.OutputDirectory;
            RaiseModeProperties();
            UpdatePreview();
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    [RelayCommand]
    private async Task SavePresetAsync()
    {
        var kind = IsVideoMode ? MediaKind.Video : MediaKind.Audio;
        var suggested = kind == MediaKind.Video
            ? $"Video {Format.VideoSummary(BuildVideoOptions())}"
            : $"{SelectedFormat.Label} {(IsQualityEnabled ? SelectedQuality.Label : string.Empty)}".Trim();
        var name = await _dialogs.PromptTextAsync("Save preset", "Name", suggested, "Save", "My preset");
        if (string.IsNullOrWhiteSpace(name)) return;

        var preset = new DownloadPreset
        {
            Name = name.Trim(),
            Kind = kind,
            Format = SelectedFormat.Value,
            Quality = SelectedQuality.Value,
            Video = kind == MediaKind.Video ? BuildVideoOptions() : null,
            FilenameTemplate = FilenameTemplate == FileNameHelper.DefaultTemplateFor(kind) ? null : FilenameTemplate,
            OutputDirectory = string.Equals(SaveToFolder.Trim(), DefaultFolder, StringComparison.OrdinalIgnoreCase) ? null : SaveToFolder.Trim(),
        };
        // Picked subtitle tracks are per-video; keep the language list instead.
        if (preset.Video is { } v && IsSingleItem && SubtitleChoices.Count > 0)
        {
            v.SubtitleLanguages = SubtitleLanguagesText;
            v.IncludeAutoSubtitles = IncludeAutoSubtitles;
        }

        await _settings.UpdateAsync(s =>
        {
            s.Presets.RemoveAll(p => string.Equals(p.Name, preset.Name, StringComparison.CurrentCultureIgnoreCase));
            s.Presets.Add(preset);
        });
        LoadPresets();
        _notifications.ShowSuccess("Preset saved", preset.Name);
    }
}
