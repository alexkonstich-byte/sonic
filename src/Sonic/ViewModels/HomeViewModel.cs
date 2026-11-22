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
using Windows.ApplicationModel.DataTransfer;

namespace Sonic.ViewModels;

public sealed partial class FormatChoice(MediaFormat? format)
{
    public MediaFormat? Format { get; } = format;

    public string Label => Format is null
        ? "Automatic (best available)"
        : string.Join(" · ", new[]
        {
            Format.AudioCodec?.Split('.')[0].ToUpperInvariant(),
            Format.AudioBitrateKbps is > 0 ? $"{Format.AudioBitrateKbps:0} kbps" : null,
            Format.SampleRate is > 0 ? $"{Format.SampleRate / 1000.0:0.#} kHz" : null,
            Format.FileSize is > 0 ? Helpers.Format.Bytes(Format.FileSize) : null,
            Format.Extension.ToUpperInvariant(),
        }.Where(s => !string.IsNullOrEmpty(s)));

    public override string ToString() => Label;
}

public sealed partial class PlaylistItemChoice(MediaInfo info, int index) : ObservableObject
{
    public MediaInfo Info { get; } = info;
    public int Index { get; } = index;
    public string Title => Info.Title;
    public string Subtitle => string.Join(" · ", new[] { Info.DisplayArtist, Info.DurationSeconds is > 0 ? Helpers.Format.Duration(Info.DurationSeconds) : null }.Where(s => !string.IsNullOrEmpty(s)));

    [ObservableProperty] private bool _isSelected = true;
}

public sealed partial class SubtitleChoice(SubtitleTrack track) : ObservableObject
{
    public SubtitleTrack Track { get; } = track;
    public string Label => Track.IsAuto ? $"{Track.Name} · auto-generated" : Track.Name;

    [ObservableProperty] private bool _isSelected;
}

public sealed partial class HomeViewModel : ObservableObject, INavigationAware
{
    private readonly IYtDlpService _ytDlp;
    private readonly IDownloadQueueService _queue;
    private readonly ILibraryService _library;
    private readonly IPlaylistService _playlists;
    private readonly ISpotifyService _spotify;
    private readonly ISpotifyDownloadService _spotifyDownloads;
    private readonly ISettingsService _settings;
    private readonly INavigationService _navigation;
    private readonly IDialogService _dialogs;
    private readonly IShellLauncher _launcher;
    private readonly IInAppNotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<HomeViewModel> _logger;
    private CancellationTokenSource? _analyzeCts;

    public HomeViewModel(
        IYtDlpService ytDlp,
        IDownloadQueueService queue,
        ILibraryService library,
        IPlaylistService playlists,
        ISpotifyService spotify,
        ISpotifyDownloadService spotifyDownloads,
        ISettingsService settings,
        INavigationService navigation,
        IDialogService dialogs,
        IShellLauncher launcher,
        IInAppNotificationService notifications,
        IUiDispatcher dispatcher,
        DownloadsStore downloads,
        ToolsStore tools,
        ILogger<HomeViewModel> logger)
    {
        _ytDlp = ytDlp;
        _queue = queue;
        _library = library;
        _playlists = playlists;
        _spotify = spotify;
        _spotifyDownloads = spotifyDownloads;
        _settings = settings;
        _navigation = navigation;
        _dialogs = dialogs;
        _launcher = launcher;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _logger = logger;
        Downloads = downloads;
        Tools = tools;
        var s = settings.Current;
        _selectedFormat = Options.Find(Options.Formats, s.DefaultFormat);
        _selectedQuality = Options.Find(Options.Qualities, s.DefaultQuality);
        _modeIndex = s.LastMediaKind == MediaKind.Video ? 1 : 0;
        _selectedContainer = Options.Find(Options.Containers, s.DefaultVideoContainer);
        _selectedVideoCodec = Options.Find(Options.VideoCodecs, s.DefaultVideoCodec);
        _selectedSubtitleMode = Options.Find(Options.SubtitleModes, s.DefaultSubtitleMode);
        _selectedSubtitleFormat = Options.Find(Options.SubtitleFormats, s.DefaultSubtitleFormat);
        _subtitleLanguagesText = s.DefaultSubtitleLanguages;
        _includeAutoSubtitles = s.IncludeAutoSubtitles;
        _embedThumbnail = s.EmbedVideoThumbnail;
        _embedChapters = s.EmbedVideoChapters;
        _filenameTemplate = _modeIndex == 1 ? s.VideoFilenameTemplate : s.FilenameTemplate;
        foreach (var q in Options.StandardVideoQualities) VideoQualityOptions.Add(q);
        _selectedVideoQuality = Options.PickHeight(VideoQualityOptions, s.DefaultVideoMaxHeight);
        UpdatePreview();
        ApplyDefaultFolder();
        LoadPresets();
        settings.Changed += (_, _) => dispatcher.Post(() =>
        {
            ApplyDefaultFolder();
            if (!_settings.Current.Presets.Select(p => p.Id).SequenceEqual(Presets.Select(p => p.Id))) LoadPresets();
        });
        SearchResults.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSearchResults));

        library.LibraryChanged += (_, _) => dispatcher.Post(() => _ = LoadRecentAsync());
        playlists.PlaylistsChanged += (_, _) => dispatcher.Post(() => _ = LoadRecentAsync());
    }

    public DownloadsStore Downloads { get; }
    public ToolsStore Tools { get; }
    public IReadOnlyList<Option<AudioFormat>> FormatOptions => Options.Formats;
    public IReadOnlyList<Option<AudioQuality>> QualityOptions => Options.Qualities;

    public ObservableCollection<Track> RecentTracks { get; } = [];
    public ObservableCollection<PlaylistSummary> RecentPlaylists { get; } = [];
    public ObservableCollection<FormatChoice> SourceFormats { get; } = [];
    public ObservableCollection<PlaylistItemChoice> PlaylistItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceName), nameof(SourceGlyph), nameof(HasUrl), nameof(IsSpotifyUrl), nameof(AnalyzeButtonText), nameof(IsSearchQuery), nameof(CanQuickDownload))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private string _url = string.Empty;

    [ObservableProperty] private SourcePlatform _detectedSource;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private bool _isAnalyzing;

    /// <summary>What the progress row says while busy ("Analyzing link…", "Finding songs on YouTube… 3 of 40").</summary>
    [ObservableProperty] private string _analyzingText = AnalyzingLinkText;

    private const string AnalyzingLinkText = "Analyzing link…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnalysis), nameof(IsSingleItem), nameof(IsPlaylist), nameof(AnalysisTitle), nameof(AnalysisSubtitle),
        nameof(AnalysisThumbnail), nameof(AnalysisDuration), nameof(AnalysisPlatform), nameof(AnalysisPlatformGlyph), nameof(PlaylistSummaryText))]
    private MediaInfo? _analysis;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCookieHint))]
    private string? _analysisError;

    /// <summary>Sign-in related failures can be solved with cookies.</summary>
    public bool ShowCookieHint => AnalysisError is { } e &&
        (e.Contains("cookies", StringComparison.OrdinalIgnoreCase) || e.Contains("not a bot", StringComparison.OrdinalIgnoreCase) || e.Contains("age-restricted", StringComparison.OrdinalIgnoreCase) || e.Contains("members", StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private async Task BatchDownloadAsync() => await OpenBatchAsync(null, null);

    /// <summary>Opens the list download dialog (also used for files/text dropped on the page).</summary>
    public async Task OpenBatchAsync(string? filePath, string? text)
    {
        var queued = await _dialogs.BatchDownloadAsync(filePath, text);
        if (queued > 0) _notifications.ShowSuccess("Added to downloads", $"{Format.Count(queued, "item")} queued from your list.");
    }

    [RelayCommand]
    private void OpenCookieSettings() => _navigation.NavigateTo(PageKey.Settings, "cookies");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQualityEnabled), nameof(QualityHint))]
    private Option<AudioFormat> _selectedFormat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityHint))]
    private Option<AudioQuality> _selectedQuality;

    [ObservableProperty] private FormatChoice? _selectedSourceFormat;
    [ObservableProperty] private int _selectedPlaylistCount;
    [ObservableProperty] private bool _hasRecentTracks;
    [ObservableProperty] private bool _hasRecentPlaylists;

    // ---- Audio / video mode ----
    public IReadOnlyList<Option<VideoContainer>> ContainerOptions => Options.Containers;
    public IReadOnlyList<Option<VideoCodecPreference>> VideoCodecOptions => Options.VideoCodecs;
    public IReadOnlyList<Option<SubtitleMode>> SubtitleModeOptions => Options.SubtitleModes;
    public IReadOnlyList<Option<SubtitleFormat>> SubtitleFormatOptions => Options.SubtitleFormats;
    public ObservableCollection<Option<int>> VideoQualityOptions { get; } = [];
    public ObservableCollection<SubtitleChoice> SubtitleChoices { get; } = [];

    [ObservableProperty] private int _modeIndex;
    [ObservableProperty] private Option<VideoContainer> _selectedContainer;
    [ObservableProperty] private Option<int>? _selectedVideoQuality;
    [ObservableProperty] private Option<VideoCodecPreference> _selectedVideoCodec;
    [ObservableProperty] private Option<SubtitleMode> _selectedSubtitleMode;
    [ObservableProperty] private Option<SubtitleFormat> _selectedSubtitleFormat;
    [ObservableProperty] private string _subtitleLanguagesText;
    [ObservableProperty] private bool _includeAutoSubtitles;
    [ObservableProperty] private bool _embedThumbnail;
    [ObservableProperty] private bool _embedChapters;
    [ObservableProperty] private string _filenameTemplate;
    [ObservableProperty] private string? _filenameError;

    // ---- Save to ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderError), nameof(HasFolderError))]
    private string _saveToFolder = string.Empty;

    [ObservableProperty] private string _defaultFolder = string.Empty;
    [ObservableProperty] private IReadOnlyList<string> _recentFolders = [];

    public string? FolderError => FileNameHelper.ValidateDirectory(SaveToFolder);
    public bool HasFolderError => FolderError is not null;

    private void ApplyDefaultFolder()
    {
        var s = _settings.Current;
        var newDefault = IsVideoMode ? s.VideoDirectory : s.DownloadDirectory;
        // Follow the default unless the user picked a different folder for this download.
        if (string.IsNullOrWhiteSpace(SaveToFolder) || string.Equals(SaveToFolder, DefaultFolder, StringComparison.OrdinalIgnoreCase))
            SaveToFolder = newDefault;
        DefaultFolder = newDefault;
        RecentFolders = s.RecentFolders.ToList();
    }
    [ObservableProperty] private string _filenamePreview = string.Empty;

    public bool IsVideoMode => ModeIndex == 1;
    public bool ShowAudioOptions => !IsVideoMode;
    public bool ShowSourceFormat => IsSingleItem && !IsVideoMode;
    public bool IsSubtitleEnabled => SelectedSubtitleMode.Value != SubtitleMode.None;
    public bool ShowSubtitleChoices => IsVideoMode && IsSubtitleEnabled && IsSingleItem && SubtitleChoices.Count > 0;
    /// <summary>Playlists: subtitle availability differs per video, so languages are typed instead of picked.</summary>
    public bool ShowSubtitleLanguageBox => IsVideoMode && IsSubtitleEnabled && IsPlaylist;
    public bool ShowNoSubtitles => IsVideoMode && IsSubtitleEnabled && IsSingleItem && SubtitleChoices.Count == 0;
    public bool ShowNoVideoWarning => IsVideoMode && IsSingleItem && Analysis?.HasVideo == false;
    public string TokensHint => "Tokens: " + string.Join("  ", FileNameHelper.TokensFor(IsVideoMode ? MediaKind.Video : MediaKind.Audio)) + "   ·   use / for folders";

    private void RaiseModeProperties()
    {
        foreach (var name in new[]
                 {
                     nameof(IsVideoMode), nameof(ShowAudioOptions), nameof(ShowSourceFormat), nameof(IsSubtitleEnabled),
                     nameof(ShowSubtitleChoices), nameof(ShowSubtitleLanguageBox), nameof(ShowNoSubtitles), nameof(ShowNoVideoWarning), nameof(TokensHint),
                     nameof(QualityHint),
                 })
            OnPropertyChanged(name);
    }

    partial void OnModeIndexChanged(int value)
    {
        var kind = value == 1 ? MediaKind.Video : MediaKind.Audio;
        FilenameTemplate = kind == MediaKind.Video ? _settings.Current.VideoFilenameTemplate : _settings.Current.FilenameTemplate;
        ApplyDefaultFolder();
        RaiseModeProperties();
        UpdatePreview();
        _ = _settings.UpdateAsync(s => s.LastMediaKind = kind);
    }

    partial void OnSelectedSubtitleModeChanged(Option<SubtitleMode> value) => RaiseModeProperties();
    partial void OnSelectedContainerChanged(Option<VideoContainer> value) => UpdatePreview();
    partial void OnSelectedVideoQualityChanged(Option<int>? value) => UpdatePreview();
    partial void OnSelectedFormatChanged(Option<AudioFormat> value) => UpdatePreview();
    partial void OnFilenameTemplateChanged(string value) => UpdatePreview();
    partial void OnAnalysisChanged(MediaInfo? value)
    {
        RaiseModeProperties();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var kind = IsVideoMode ? MediaKind.Video : MediaKind.Audio;
        FilenameError = FileNameHelper.ValidateTemplate(FilenameTemplate, kind);
        if (FilenameError is not null)
        {
            FilenamePreview = string.Empty;
            return;
        }

        var info = Analysis is { IsPlaylist: false } single ? single : Analysis?.Entries.FirstOrDefault();
        var title = info?.Title ?? "Example title";
        var uploader = info?.Uploader ?? "Channel";
        if (kind == MediaKind.Video)
        {
            var height = SelectedVideoQuality?.Value is > 0 and var h ? Math.Min(h, info?.VideoHeights.FirstOrDefault() is > 0 and var max ? max : h) : info?.VideoHeights.FirstOrDefault();
            FilenamePreview = FileNameHelper.ExpandVideoTemplate(FilenameTemplate, title, uploader, info?.Id ?? "dQw4w9WgXcQ", info?.UploadDate ?? DateTime.Today.ToString("yyyyMMdd"), height)
                              + "." + SelectedContainer.Value.ToString().ToLowerInvariant();
        }
        else
        {
            var meta = new TrackMetadata { Title = info?.Track ?? TitleParser.Clean(title), Artist = info?.Artist, Album = info?.Album, Year = info?.ReleaseYear };
            if (info?.Track is null && info?.Artist is null && TitleParser.TrySplitArtistTitle(title, out var a, out var t))
            {
                meta.Artist = a;
                meta.Title = t;
            }

            meta.Artist ??= uploader;
            var ext = SelectedFormat.Value == AudioFormat.Original ? ".m4a" : FileNameHelper.GetExtension(SelectedFormat.Value);
            FilenamePreview = FileNameHelper.ExpandTemplate(FilenameTemplate, meta, uploader, info?.Id) + ext;
        }
    }

    [RelayCommand]
    private void ResetFilenameTemplate() => FilenameTemplate = FileNameHelper.DefaultTemplateFor(IsVideoMode ? MediaKind.Video : MediaKind.Audio);

    private void BuildVideoChoices(MediaInfo info)
    {
        var s = _settings.Current;
        VideoQualityOptions.Clear();
        VideoQualityOptions.Add(Options.HeightOption(0));
        var heights = info.IsPlaylist || info.VideoHeights.Count == 0 ? Options.StandardHeights : info.VideoHeights.Where(h => h >= 144);
        foreach (var h in heights) VideoQualityOptions.Add(Options.HeightOption(h));
        SelectedVideoQuality = Options.PickHeight(VideoQualityOptions, s.DefaultVideoMaxHeight);

        SubtitleChoices.Clear();
        var wanted = (SubtitleLanguagesText ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var choices = info.Subtitles.Select(track =>
        {
            var code = track.IsAuto && track.Code.EndsWith("-orig", StringComparison.Ordinal) ? track.Code[..^5] : track.Code;
            var matches = wanted.Any(w => string.Equals(w, code, StringComparison.OrdinalIgnoreCase) || code.StartsWith(w + "-", StringComparison.OrdinalIgnoreCase));
            return new SubtitleChoice(track) { IsSelected = matches && (!track.IsAuto || IncludeAutoSubtitles) };
        });
        // Preferred languages first, then manual subtitles before automatic captions, alphabetically.
        foreach (var choice in choices.OrderByDescending(c => c.IsSelected).ThenBy(c => c.Track.IsAuto).ThenBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase))
            SubtitleChoices.Add(choice);

        RaiseModeProperties();
    }

    private VideoOptions BuildVideoOptions()
    {
        string languages;
        bool includeAuto;
        if (IsSingleItem && SubtitleChoices.Count > 0)
        {
            var selected = SubtitleChoices.Where(c => c.IsSelected).ToList();
            languages = string.Join(',', selected.Select(c => c.Track.Code));
            includeAuto = selected.Any(c => c.Track.IsAuto);
        }
        else
        {
            languages = SubtitleLanguagesText ?? string.Empty;
            includeAuto = IncludeAutoSubtitles;
        }

        return new VideoOptions
        {
            Container = SelectedContainer.Value,
            MaxHeight = SelectedVideoQuality?.Value ?? 0,
            Codec = SelectedVideoCodec.Value,
            SubtitleMode = SelectedSubtitleMode.Value,
            SubtitleFormat = SelectedSubtitleFormat.Value,
            SubtitleLanguages = languages,
            IncludeAutoSubtitles = includeAuto,
            EmbedThumbnail = EmbedThumbnail,
            EmbedChapters = EmbedChapters,
        };
    }

    private DownloadRequest CreateRequest(MediaInfo info, string? title, string? artist) => new()
    {
        Url = info.Url,
        Kind = IsVideoMode ? MediaKind.Video : MediaKind.Audio,
        Video = IsVideoMode ? BuildVideoOptions() : null,
        FilenameTemplate = FilenameTemplate,
        OutputDirectory = SaveToFolder.Trim(),
        Title = title,
        Artist = artist,
        ThumbnailUrl = info.ThumbnailUrl,
        DurationSeconds = info.DurationSeconds,
        FormatId = IsVideoMode || info.IsPlaylist ? null : SelectedSourceFormat?.Format?.FormatId,
        Format = SelectedFormat.Value,
        Quality = SelectedQuality.Value,
    };

    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
    public bool IsSpotifyUrl => DetectedSource == SourcePlatform.Spotify;

    /// <summary>Spotify playlists: name files "01 - Artist - Title" in playlist order (shared with Download all).</summary>
    public bool NumberPlaylistFiles
    {
        get => _numberPlaylistFiles ??= _settings.Current.NumberPlaylistDownloads;
        set
        {
            if (value == NumberPlaylistFiles) return;
            _numberPlaylistFiles = value;
            _ = _settings.UpdateAsync(s => s.NumberPlaylistDownloads = value);
            OnPropertyChanged();
        }
    }

    private bool? _numberPlaylistFiles;
    public string AnalyzeButtonText => IsSpotifyUrl ? "Download playlist" : IsSearchQuery ? "Search" : "Analyze";
    public string SourceName => HasUrl ? SourceDetector.GetDisplayName(DetectedSource) : string.Empty;
    public string SourceGlyph => Glyphs.ForSource(DetectedSource);

    public bool HasAnalysis => Analysis is not null;
    public bool IsPlaylist => Analysis?.IsPlaylist == true;
    public bool IsSingleItem => Analysis is { IsPlaylist: false };
    public string AnalysisTitle => Analysis?.Title ?? string.Empty;
    public string AnalysisSubtitle => Analysis?.DisplayArtist ?? string.Empty;
    public string? AnalysisThumbnail => Analysis?.ThumbnailUrl ?? Analysis?.Entries.FirstOrDefault()?.ThumbnailUrl;
    public string AnalysisDuration => Analysis?.DurationSeconds is > 0 ? Format.Duration(Analysis.DurationSeconds) : string.Empty;
    public string AnalysisPlatform => Analysis is null ? string.Empty : SourceDetector.GetDisplayName(Analysis.Source);
    public string AnalysisPlatformGlyph => Glyphs.ForSource(Analysis?.Source ?? SourcePlatform.Unknown);
    public string PlaylistSummaryText => Analysis is { IsPlaylist: true } a
        ? $"{Format.Count(a.Entries.Count, "item")}" + (a.Entries.Sum(e => e.DurationSeconds ?? 0) is > 0 and var total ? $" · {Format.LongDuration(total)}" : string.Empty)
        : string.Empty;

    public bool IsQualityEnabled => SelectedFormat.Value is not (AudioFormat.Flac or AudioFormat.Wav or AudioFormat.Original);

    public string QualityHint => IsVideoMode ? Format.VideoSummary(BuildVideoOptions()) : SelectedFormat.Value switch
    {
        AudioFormat.Original => "The source audio is kept as-is (no re-encoding).",
        AudioFormat.Flac or AudioFormat.Wav => "Lossless output. Note: converting a lossy source to FLAC/WAV does not add quality.",
        _ => Options.QualityLabel(SelectedQuality.Value, SelectedFormat.Value),
    };

    partial void OnUrlChanged(string value)
    {
        DetectedSource = SourceDetector.Detect(value);
        AnalysisError = null;
    }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        await LoadRecentAsync();
        // Opened with a link (clipboard offer, sonic:// link, YouTube page…): analyze it right away.
        if (parameter is string url && !string.IsNullOrWhiteSpace(url))
        {
            Url = url;
            if (AnalyzeCommand.CanExecute(null)) await AnalyzeCommand.ExecuteAsync(null);
        }
    }

    public void OnNavigatedFrom() { }

    private async Task LoadRecentAsync()
    {
        try
        {
            var tracks = await _library.GetRecentlyAddedAsync(8);
            RecentTracks.Clear();
            foreach (var t in tracks) RecentTracks.Add(t);
            HasRecentTracks = RecentTracks.Count > 0;

            var playlists = await _playlists.GetPlaylistsAsync();
            RecentPlaylists.Clear();
            foreach (var p in playlists.Where(p => p.SourceType == PlaylistSource.Spotify).OrderByDescending(p => p.CreatedAt).Take(4))
                RecentPlaylists.Add(p);
            HasRecentPlaylists = RecentPlaylists.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading recent items failed");
        }
    }

    [RelayCommand]
    private async Task PasteAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                _notifications.ShowInfo("Nothing to paste", "The clipboard doesn't contain a link.");
                return;
            }

            var text = (await content.GetTextAsync()).Trim();
            Url = text;
            if (SourceDetector.TryNormalizeUrl(text, out _)) await AnalyzeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard read failed");
            _notifications.ShowWarning("Couldn't read the clipboard", ex.Message);
        }
    }

    private bool CanAnalyze() => HasUrl && !IsAnalyzing;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (IsSearchQuery)
        {
            await SearchAsync();
            return;
        }

        if (!SourceDetector.TryNormalizeUrl(Url, out var uri))
        {
            AnalysisError = "That doesn't look like a valid link. Paste a full address such as https://www.youtube.com/watch?v=…";
            return;
        }

        if (IsSpotifyUrl)
        {
            await DownloadSpotifyAsync(uri.AbsoluteUri);
            return;
        }

        if (SourceDetector.IsProtectedStreamingService(uri.AbsoluteUri))
        {
            AnalysisError = "This is a DRM-protected streaming service, so its audio can't be downloaded. Use the service's official offline mode instead.";
            return;
        }

        _analyzeCts?.Cancel();
        _analyzeCts = new CancellationTokenSource();
        var token = _analyzeCts.Token;
        IsAnalyzing = true;
        AnalysisError = null;
        Analysis = null;
        PreviousDownload = null;
        ClearSearch();
        SourceFormats.Clear();
        PlaylistItems.Clear();
        try
        {
            var info = await Task.Run(() => _ytDlp.AnalyzeAsync(uri.AbsoluteUri, token), token);
            if (token.IsCancellationRequested) return;
            if (info.IsLive)
            {
                AnalysisError = "Live streams can't be downloaded while they're live.";
                return;
            }

            SourceFormats.Add(new FormatChoice(null));
            foreach (var f in info.AudioFormats) SourceFormats.Add(new FormatChoice(f));
            SelectedSourceFormat = SourceFormats[0];

            var index = 1;
            foreach (var entry in info.Entries)
            {
                var choice = new PlaylistItemChoice(entry, index++);
                choice.PropertyChanged += (_, _) => SelectedPlaylistCount = PlaylistItems.Count(p => p.IsSelected);
                PlaylistItems.Add(choice);
            }

            SelectedPlaylistCount = PlaylistItems.Count;
            BuildVideoChoices(info);
            Analysis = info;
            await CheckPreviousDownloadAsync(info);
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
            App.GetService<IZapretAdvisor>().MaybeSuggest(ex.Message, uri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Analyze failed");
            AnalysisError = "Something went wrong while analyzing the link: " + ex.Message;
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    [RelayCommand]
    private void CancelAnalyze()
    {
        _analyzeCts?.Cancel();
        IsAnalyzing = false;
        AnalyzingText = AnalyzingLinkText;
    }

    /// <summary>A Spotify link typed or pasted into the box starts downloading right away.</summary>
    public void OnUrlPasted()
    {
        if (IsSpotifyUrl && AnalyzeCommand.CanExecute(null)) AnalyzeCommand.Execute(null);
    }

    /// <summary>
    /// Spotify playlist or album link: import it as a playlist, link songs that are already on disk and queue the rest
    /// from YouTube with the current audio options — no dialogs.
    /// </summary>
    private async Task DownloadSpotifyAsync(string url)
    {
        if (!_spotify.IsConfigured)
        {
            // The import dialog explains how to add the Spotify Client ID and Secret.
            var id = await _dialogs.ImportSpotifyAsync(url);
            if (id is { } playlistId) _navigation.NavigateTo(PageKey.PlaylistDetail, playlistId);
            return;
        }

        // Spotify songs are always audio; in Video mode the audio defaults from Settings are used.
        var folder = IsVideoMode ? _settings.Current.DownloadDirectory : SaveToFolder.Trim();
        var template = IsVideoMode ? _settings.Current.FilenameTemplate : FilenameTemplate;
        if (FileNameHelper.ValidateTemplate(template, MediaKind.Audio) is { } templateError)
        {
            await _dialogs.ShowErrorAsync("Check the file name", templateError);
            return;
        }

        if (FileNameHelper.ValidateDirectory(folder) is { } folderError)
        {
            await _dialogs.ShowErrorAsync("Check the folder", folderError);
            return;
        }

        var options = new PlaylistDownloadOptions(
            folder,
            IsVideoMode ? _settings.Current.DefaultFormat : SelectedFormat.Value,
            IsVideoMode ? _settings.Current.DefaultQuality : SelectedQuality.Value,
            template,
            NumberPlaylistFiles);

        _analyzeCts?.Cancel();
        _analyzeCts = new CancellationTokenSource();
        var token = _analyzeCts.Token;
        IsAnalyzing = true;
        AnalyzingText = "Loading the playlist from Spotify…";
        AnalysisError = null;
        Analysis = null;
        PreviousDownload = null;
        ClearSearch();
        SourceFormats.Clear();
        PlaylistItems.Clear();
        try
        {
            var progress = new Progress<string>(s =>
            {
                if (!token.IsCancellationRequested) AnalyzingText = s;
            });
            var r = await Task.Run(() => _spotifyDownloads.DownloadAsync(url, options, progress, token), token);
            if (token.IsCancellationRequested) return;
            Url = string.Empty;

            var have = r.InLibrary + r.Linked;
            var parts = new List<string>();
            if (r.Queued > 0) parts.Add($"{Format.Count(r.Queued, "song")} queued");
            if (have > 0) parts.Add($"{have} already downloaded");
            if (r.AlreadyQueued > 0) parts.Add($"{r.AlreadyQueued} already in the queue");
            if (r.NotFound.Count > 0)
            {
                var names = string.Join(", ", r.NotFound.Take(3).Select(e => $"{e.Artist} – {e.Title}"));
                parts.Add($"{r.NotFound.Count} not found on YouTube ({names}{(r.NotFound.Count > 3 ? ", …" : string.Empty)}). Open the playlist and use Find source on those");
            }

            if (r.Failed.Count > 0)
                parts.Add($"{r.Failed.Count} couldn't be searched (check your connection or zapret) — paste the link again to retry them");

            var title = $"\"{r.Playlist.Name}\"";
            if (r.Queued == 0 && r.NotFound.Count == 0 && r.Failed.Count == 0)
                _notifications.ShowInfo(title, $"Nothing new to download: {string.Join(" · ", parts)}.");
            else if (r.Queued == 0)
                _notifications.ShowWarning(title, string.Join(" · ", parts) + ".");
            else
                _notifications.ShowSuccess(title, string.Join(" · ", parts) + ".");
        }
        catch (OperationCanceledException)
        {
            _notifications.ShowInfo("Stopped", "Songs found so far stay in the download queue.");
        }
        catch (SpotifyException ex)
        {
            AnalysisError = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spotify download failed");
            AnalysisError = "Something went wrong while downloading the playlist: " + ex.Message;
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsAnalyzing = false;
                AnalyzingText = AnalyzingLinkText;
            }
        }
    }

    [RelayCommand]
    private void ClearAnalysis()
    {
        Analysis = null;
        AnalysisError = null;
        PreviousDownload = null;
        Url = string.Empty;
        SourceFormats.Clear();
        PlaylistItems.Clear();
        SubtitleChoices.Clear();
        RaiseModeProperties();
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (Analysis is not { } info) return;
        if (FilenameError is not null)
        {
            await _dialogs.ShowErrorAsync("Check the file name", FilenameError);
            return;
        }

        if (FolderError is { } folderError)
        {
            await _dialogs.ShowErrorAsync("Check the folder", folderError);
            return;
        }

        try
        {
            await _queue.EnqueueAsync(CreateRequest(info, IsVideoMode ? info.Title : info.Track ?? info.Title, info.DisplayArtist));
            _notifications.ShowSuccess("Added to downloads", info.Title);
            ClearAnalysis();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await _dialogs.ShowErrorAsync("Couldn't add the download", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        var selected = PlaylistItems.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0) return;
        if (FilenameError is not null)
        {
            await _dialogs.ShowErrorAsync("Check the file name", FilenameError);
            return;
        }

        if (FolderError is { } folderError)
        {
            await _dialogs.ShowErrorAsync("Check the folder", folderError);
            return;
        }

        var added = 0;
        foreach (var item in selected)
        {
            try
            {
                await _queue.EnqueueAsync(CreateRequest(item.Info, item.Info.Title, item.Info.DisplayArtist));
                added++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Skipping playlist entry {Url}", item.Info.Url);
            }
        }

        _notifications.ShowSuccess("Added to downloads", $"{Format.Count(added, "item")} queued from \"{Analysis?.Title}\".");
        ClearAnalysis();
    }

    [RelayCommand]
    private void SelectAll(bool select)
    {
        foreach (var p in PlaylistItems) p.IsSelected = select;
    }

    [RelayCommand]
    private void OpenMusicFolder()
    {
        var dir = _settings.Current.DownloadDirectory;
        Directory.CreateDirectory(dir);
        _launcher.OpenFolder(dir);
    }

    [RelayCommand]
    private async Task ImportSpotifyAsync()
    {
        var id = await _dialogs.ImportSpotifyAsync(null);
        if (id is { } playlistId) _navigation.NavigateTo(PageKey.PlaylistDetail, playlistId);
    }

    [RelayCommand]
    private async Task ScanLibraryAsync()
    {
        if (_library.IsScanning)
        {
            _notifications.ShowInfo("Already scanning", "The library is being scanned.");
            return;
        }

        _notifications.ShowInfo("Scanning library", "Looking for new and changed music…");
        try
        {
            var result = await Task.Run(() => _library.ScanAsync(null));
            _notifications.ShowSuccess("Library updated", $"{result.Added} added, {result.Updated} updated, {result.Removed} removed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed");
            _notifications.ShowError("Scan failed", ex.Message);
        }
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        if (Enum.TryParse<PageKey>(page, out var key)) _navigation.NavigateTo(key);
    }

    [RelayCommand]
    private void OpenPlaylist(PlaylistSummary playlist) => _navigation.NavigateTo(PageKey.PlaylistDetail, playlist.Id);

    [RelayCommand]
    private Task PlayTrack(Track track) => App.GetService<PlayerViewModel>().PlayTrackAsync(track, RecentTracks.ToList());
}
