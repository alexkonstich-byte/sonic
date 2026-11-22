using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;
using Sonic.Core.Services;
using Sonic.Helpers;
using Sonic.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Sonic.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, INavigationAware
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IStartupService _startup;
    private readonly IWindowService _windows;
    private readonly IShellLauncher _launcher;
    private readonly IDialogService _dialogs;
    private readonly IDependencyService _dependencies;
    private readonly ISpotifyService _spotify;
    private readonly ILibraryService _library;
    private readonly IAppPaths _paths;
    private readonly IInAppNotificationService _notifications;
    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly ICookieService _cookies;
    private readonly IZapretService _zapret;
    private readonly ITrayService _tray;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<SettingsViewModel> _logger;
    private bool _loading;

    public SettingsViewModel(
        ISettingsService settings,
        IThemeService theme,
        IStartupService startup,
        IWindowService windows,
        IShellLauncher launcher,
        IDialogService dialogs,
        IDependencyService dependencies,
        ISpotifyService spotify,
        ILibraryService library,
        IAppPaths paths,
        IInAppNotificationService notifications,
        LoggingLevelSwitch levelSwitch,
        ToolsStore tools,
        ICookieService cookies,
        IZapretService zapret,
        ITrayService tray,
        IUiDispatcher dispatcher,
        ILogger<SettingsViewModel> logger)
    {
        _cookies = cookies;
        _zapret = zapret;
        _tray = tray;
        _dispatcher = dispatcher;
        _selectedSponsorBlockVideo = SponsorBlockModes[0];
        _selectedCookieMode = CookieModeOptions[0];
        _settings = settings;
        _theme = theme;
        _startup = startup;
        _windows = windows;
        _launcher = launcher;
        _dialogs = dialogs;
        _dependencies = dependencies;
        _spotify = spotify;
        _library = library;
        _paths = paths;
        _notifications = notifications;
        _levelSwitch = levelSwitch;
        _logger = logger;
        Tools = tools;
        _selectedTheme = Options.Themes[0];
        _selectedStartupPage = Options.StartupPages[0];
        _selectedFormat = Options.Formats[0];
        _selectedQuality = Options.Qualities[0];
        _selectedDuplicateHandling = Options.DuplicateHandlings[0];
        Load();
    }

    public ToolsStore Tools { get; }
    public IReadOnlyList<Option<AppTheme>> ThemeOptions => Options.Themes;
    public IReadOnlyList<Option<StartupPage>> StartupPageOptions => Options.StartupPages;
    public IReadOnlyList<Option<AudioFormat>> FormatOptions => Options.Formats;
    public IReadOnlyList<Option<AudioQuality>> QualityOptions => Options.Qualities;
    public IReadOnlyList<Option<DuplicateHandling>> DuplicateOptions => Options.DuplicateHandlings;
    public ObservableCollection<string> MusicFolders { get; } = [];

    public string AppVersion => ShellViewModel.AppVersion;
    public string DataDirectory => _paths.DataDirectory;
    public string LogsDirectory => _paths.LogsDirectory;

    /// <summary>Settings section to bring into view (set by navigation parameter).</summary>
    [ObservableProperty] private string? _focusSection;

    // General
    [ObservableProperty] private Option<AppTheme> _selectedTheme;
    [ObservableProperty] private bool _launchAtSignIn;
    [ObservableProperty] private Option<StartupPage> _selectedStartupPage;
    [ObservableProperty] private bool _resumeDownloadsOnStartup;
    [ObservableProperty] private bool _showNotifications;
    [ObservableProperty] private bool _confirmExit;

    // Downloads
    [ObservableProperty] private string _downloadDirectory = string.Empty;
    [ObservableProperty] private string _filenameTemplate = string.Empty;
    [ObservableProperty] private string? _filenameTemplateError;
    [ObservableProperty] private string _filenamePreview = string.Empty;
    [ObservableProperty] private double _maxConcurrentDownloads = 3;
    [ObservableProperty] private Option<AudioFormat> _selectedFormat;
    [ObservableProperty] private Option<AudioQuality> _selectedQuality;
    [ObservableProperty] private bool _embedArtwork;
    [ObservableProperty] private bool _cropArtwork;
    [ObservableProperty] private bool _cleanTitles;
    [ObservableProperty] private double _maxRetries;

    // Integrations
    [ObservableProperty] private string _spotifyClientId = string.Empty;
    [ObservableProperty] private string _spotifyClientSecret = string.Empty;
    [ObservableProperty] private bool _isSpotifyConfigured;
    [ObservableProperty] private bool _isTestingSpotify;
    [ObservableProperty] private string? _spotifyStatus;
    [ObservableProperty] private bool _checkForToolUpdates;
    [ObservableProperty] private bool _isCheckingTools;
    [ObservableProperty] private string? _ytDlpOverride;
    [ObservableProperty] private string? _ffmpegOverride;

    // Video
    public IReadOnlyList<Option<VideoContainer>> ContainerOptions => Options.Containers;
    public IReadOnlyList<Option<int>> VideoQualityOptions => Options.StandardVideoQualities;
    public IReadOnlyList<Option<VideoCodecPreference>> VideoCodecOptions => Options.VideoCodecs;
    public IReadOnlyList<Option<SubtitleMode>> SubtitleModeOptions => Options.SubtitleModes;
    public IReadOnlyList<Option<SubtitleFormat>> SubtitleFormatOptions => Options.SubtitleFormats;

    [ObservableProperty] private string _videoDirectory = string.Empty;
    [ObservableProperty] private string _videoFilenameTemplate = string.Empty;
    [ObservableProperty] private string? _videoFilenameTemplateError;
    [ObservableProperty] private string _videoFilenamePreview = string.Empty;
    [ObservableProperty] private Option<VideoContainer> _selectedVideoContainer = Options.Containers[0];
    [ObservableProperty] private Option<int> _selectedVideoQuality = Options.StandardVideoQualities[0];
    [ObservableProperty] private Option<VideoCodecPreference> _selectedVideoCodec = Options.VideoCodecs[0];
    [ObservableProperty] private Option<SubtitleMode> _selectedSubtitleMode = Options.SubtitleModes[0];
    [ObservableProperty] private Option<SubtitleFormat> _selectedSubtitleFormat = Options.SubtitleFormats[0];
    [ObservableProperty] private string _subtitleLanguages = string.Empty;
    [ObservableProperty] private bool _includeAutoSubtitles;
    [ObservableProperty] private bool _embedVideoThumbnail;
    [ObservableProperty] private bool _embedVideoChapters;

    partial void OnVideoFilenameTemplateChanged(string value)
    {
        VideoFilenameTemplateError = FileNameHelper.ValidateTemplate(value, MediaKind.Video);
        VideoFilenamePreview = VideoFilenameTemplateError is null
            ? FileNameHelper.ExpandVideoTemplate(value, "Big Buck Bunny", "Blender", "aqz-KE-bpKQ", "20140110", 1080) + ".mp4"
            : string.Empty;
        if (VideoFilenameTemplateError is null) Save(s => s.VideoFilenameTemplate = value.Trim());
    }

    partial void OnSelectedVideoContainerChanged(Option<VideoContainer> value) { if (value is not null) Save(s => s.DefaultVideoContainer = value.Value); }
    partial void OnSelectedVideoQualityChanged(Option<int> value) { if (value is not null) Save(s => s.DefaultVideoMaxHeight = value.Value); }
    partial void OnSelectedVideoCodecChanged(Option<VideoCodecPreference> value) { if (value is not null) Save(s => s.DefaultVideoCodec = value.Value); }
    partial void OnSelectedSubtitleModeChanged(Option<SubtitleMode> value) { if (value is not null) Save(s => s.DefaultSubtitleMode = value.Value); }
    partial void OnSelectedSubtitleFormatChanged(Option<SubtitleFormat> value) { if (value is not null) Save(s => s.DefaultSubtitleFormat = value.Value); }
    partial void OnSubtitleLanguagesChanged(string value) => Save(s => s.DefaultSubtitleLanguages = value);
    partial void OnIncludeAutoSubtitlesChanged(bool value) => Save(s => s.IncludeAutoSubtitles = value);
    partial void OnEmbedVideoThumbnailChanged(bool value) => Save(s => s.EmbedVideoThumbnail = value);
    partial void OnEmbedVideoChaptersChanged(bool value) => Save(s => s.EmbedVideoChapters = value);
    // Network
    [ObservableProperty] private double _speedLimitKBps;
    [ObservableProperty] private string _proxyUrl = string.Empty;
    [ObservableProperty] private string? _proxyError;

    partial void OnSpeedLimitKBpsChanged(double value)
    {
        if (double.IsNaN(value)) return;
        Save(s => s.SpeedLimitKBps = (int)Math.Max(0, value));
    }

    partial void OnProxyUrlChanged(string value)
    {
        ProxyError = NetworkOptions.ValidateProxy(value);
        if (ProxyError is null) Save(s => s.ProxyUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    // SponsorBlock
    public IReadOnlyList<Option<SponsorBlockMode>> SponsorBlockModes { get; } =
    [
        new(SponsorBlockMode.Off, "Off"),
        new(SponsorBlockMode.Mark, "Mark sponsor segments as chapters"),
        new(SponsorBlockMode.Remove, "Cut sponsor segments out"),
    ];

    [ObservableProperty] private bool _sponsorBlockAudio;
    [ObservableProperty] private Option<SponsorBlockMode> _selectedSponsorBlockVideo = null!;

    partial void OnSponsorBlockAudioChanged(bool value) => Save(s => s.SponsorBlockAudio = value);
    partial void OnSelectedSponsorBlockVideoChanged(Option<SponsorBlockMode> value) { if (value is not null) Save(s => s.SponsorBlockVideo = value.Value); }

    // Smart downloads
    [ObservableProperty] private bool _pauseWhileGaming;
    [ObservableProperty] private bool _autoRetryOnReconnect;
    [ObservableProperty] private bool _warnAlreadyDownloaded;
    public System.Collections.ObjectModel.ObservableCollection<DownloadPreset> Presets { get; } = [];

    partial void OnPauseWhileGamingChanged(bool value) => Save(s => s.PauseWhileGaming = value);

    // Steam apps that never pause downloads (tools such as Wallpaper Engine).
    public ObservableCollection<SteamApp> IgnoredApps { get; } = [];
    public ObservableCollection<SteamApp> AddableApps { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddIgnoredAppCommand))]
    private SteamApp? _appToIgnore;

    [ObservableProperty] private bool _hasIgnoredApps;

    private void LoadIgnoredApps()
    {
        var ignored = _settings.Current.GameModeIgnoredApps;
        IgnoredApps.Clear();
        foreach (var id in ignored)
            IgnoredApps.Add(new SteamApp(id, GameModeService.GetSteamAppName(id) ?? (id == 431960 ? "Wallpaper Engine" : $"Steam app {id}")));
        HasIgnoredApps = IgnoredApps.Count > 0;

        AddableApps.Clear();
        foreach (var app in GameModeService.GetInstalledSteamApps().Where(a => !ignored.Contains(a.Id))) AddableApps.Add(app);
        AppToIgnore = null;
    }

    private bool CanAddIgnoredApp() => AppToIgnore is not null;

    [RelayCommand(CanExecute = nameof(CanAddIgnoredApp))]
    private async Task AddIgnoredAppAsync()
    {
        if (AppToIgnore is not { } app) return;
        await SaveAsync(s =>
        {
            if (!s.GameModeIgnoredApps.Contains(app.Id)) s.GameModeIgnoredApps.Add(app.Id);
        });
        LoadIgnoredApps();
    }

    [RelayCommand]
    private async Task RemoveIgnoredAppAsync(SteamApp app)
    {
        await SaveAsync(s => s.GameModeIgnoredApps.Remove(app.Id));
        LoadIgnoredApps();
    }
    partial void OnAutoRetryOnReconnectChanged(bool value) => Save(s => s.AutoRetryOnReconnect = value);
    partial void OnWarnAlreadyDownloadedChanged(bool value) => Save(s => s.WarnAlreadyDownloaded = value);

    [RelayCommand]
    private async Task DeletePresetAsync(DownloadPreset preset)
    {
        await SaveAsync(s => s.Presets.RemoveAll(p => p.Id == preset.Id));
        Presets.Remove(preset);
    }

    [RelayCommand]
    private async Task RestorePresetsAsync()
    {
        await SaveAsync(s =>
        {
            foreach (var d in DownloadPreset.Defaults().Where(d => s.Presets.All(p => p.Id != d.Id))) s.Presets.Add(d);
        });
        Presets.Clear();
        foreach (var p in _settings.Current.Presets) Presets.Add(p);
    }

    // Updates
    public UpdateService Updates { get; } = App.GetService<UpdateService>();
    [ObservableProperty] private bool _autoCheckUpdates;
    [ObservableProperty] private string _updateFeed = string.Empty;
    public string UpdateFeedPlaceholder => UpdateService.BuiltInFeed ?? "Folder, web address or https://github.com/owner/repo";

    partial void OnAutoCheckUpdatesChanged(bool value) => Save(s => s.AutoCheckUpdates = value);
    partial void OnUpdateFeedChanged(string value) => Save(s => s.UpdateFeed = string.IsNullOrWhiteSpace(value) ? null : value.Trim());

    public ShareIntegrationService Share { get; } = App.GetService<ShareIntegrationService>();

    // Spotify liked songs
    public LikedSongsService Liked { get; } = App.GetService<LikedSongsService>();
    public IReadOnlyList<string> LikedModes { get; } = ["Only songs I like from now on", "All my liked songs"];
    [ObservableProperty] private bool _likedSync;
    [ObservableProperty] private int _likedModeIndex;
    [ObservableProperty] private string _likedFolder = string.Empty;
    public string LikedDefaultFolder => _settings.Current.DownloadDirectory;
    public IReadOnlyList<string> LikedRecentFolders => _settings.Current.RecentFolders.ToList();

    partial void OnLikedSyncChanged(bool value)
    {
        if (_loading) return;
        _ = SaveAsync(s =>
        {
            s.SpotifyLikedSync = value;
            // Starting fresh: "from now on" means likes after this moment.
            if (value && LikedModeIndex == 0 && s.SpotifyLikedSyncFrom is null) s.SpotifyLikedSyncFrom = DateTime.UtcNow;
        }).ContinueWith(_ => _dispatcher.Post(() =>
        {
            if (value) _ = Liked.SyncAsync(userInitiated: true);
        }), TaskScheduler.Default);
    }

    partial void OnLikedModeIndexChanged(int value) => Save(s => s.SpotifyLikedSyncFrom = value == 1 ? null : s.SpotifyLikedSyncFrom ?? DateTime.UtcNow);

    partial void OnLikedFolderChanged(string value)
    {
        if (Sonic.Core.Helpers.FileNameHelper.ValidateDirectory(value) is not null) return;
        Save(s => s.SpotifyLikedSyncFolder = string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), s.DownloadDirectory, StringComparison.OrdinalIgnoreCase) ? null : value.Trim());
    }

    [RelayCommand]
    private void CopyRedirectUri()
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(Liked.RedirectUri);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        _notifications.ShowInfo("Copied", Liked.RedirectUri);
    }

    // Discord
    [ObservableProperty] private bool _discordPresence;
    [ObservableProperty] private string _discordClientId = string.Empty;
    public DiscordPresenceService Discord { get; } = App.GetService<DiscordPresenceService>();
    public string? DiscordIdError => string.IsNullOrWhiteSpace(DiscordClientId) || DiscordClientId.Trim().All(char.IsAsciiDigit) ? null : "The Application ID is a long number.";

    partial void OnDiscordPresenceChanged(bool value) => Save(s => s.DiscordPresence = value);

    partial void OnDiscordClientIdChanged(string value)
    {
        OnPropertyChanged(nameof(DiscordIdError));
        if (DiscordIdError is null) Save(s => s.DiscordClientId = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    [RelayCommand]
    private void OpenDiscordPortal() => _launcher.OpenUri("https://discord.com/developers/applications");

    // Desktop integration
    [ObservableProperty] private bool _watchClipboard;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _globalHotkey;
    [ObservableProperty] private bool _protocolHandler;
    [ObservableProperty] private string _hotkeyStatus = string.Empty;

    public string AudioHotkeyText => TrayService.AudioHotkeyText;
    public string VideoHotkeyText => TrayService.VideoHotkeyText;

    partial void OnWatchClipboardChanged(bool value) => Save(s => s.WatchClipboard = value);
    partial void OnMinimizeToTrayChanged(bool value) => Save(s => s.MinimizeToTray = value);
    partial void OnProtocolHandlerChanged(bool value) => Save(s => s.ProtocolHandler = value);

    partial void OnGlobalHotkeyChanged(bool value)
    {
        if (_loading) return;
        _ = SaveAsync(s => s.GlobalHotkey = value).ContinueWith(_ => _dispatcher.Post(UpdateHotkeyStatus), TaskScheduler.Default);
    }

    private void UpdateHotkeyStatus()
    {
        _tray.ApplySettings();
        HotkeyStatus = !GlobalHotkey ? "Off"
            : _tray.HotkeysRegistered ? $"{AudioHotkeyText}: audio · {VideoHotkeyText}: video"
            : "Another app is already using these shortcuts.";
    }

    [RelayCommand]
    private void CopyBookmarklet(string kind)
    {
        var package = new DataPackage();
        package.SetText(kind == "video" ? ProtocolRegistration.VideoBookmarklet : ProtocolRegistration.AudioBookmarklet);
        Clipboard.SetContent(package);
        _notifications.ShowInfo("Bookmarklet copied", "Create a bookmark in your browser and paste this as its address. Clicking it on a video page sends it to Sonic.");
    }

    // zapret
    [ObservableProperty] private string _zapretStatusText = string.Empty;
    [ObservableProperty] private string? _zapretFolder;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartZapret))]
    private bool _isZapretInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartZapret))]
    private bool _isZapretRunning;

    public bool CanStartZapret => IsZapretInstalled && !IsZapretRunning;
    [ObservableProperty] private bool _isZapretBusy;
    [ObservableProperty] private string? _zapretMessage;
    [ObservableProperty] private bool _zapretMessageIsError;
    [ObservableProperty] private string? _selectedZapretStrategy;
    [ObservableProperty] private bool _zapretAutoStart;
    [ObservableProperty] private bool _zapretSuggest;
    [ObservableProperty] private string? _reachabilityText;
    public ObservableCollection<string> ZapretStrategies { get; } = [];

    [ObservableProperty] private bool _zapretAutoInstall;
    [ObservableProperty] private bool _autoInstallTools;

    partial void OnZapretAutoInstallChanged(bool value) => Save(s => s.ZapretAutoInstall = value);
    partial void OnAutoInstallToolsChanged(bool value) => Save(s => s.AutoInstallTools = value);

    [RelayCommand]
    private async Task InstallZapretAsync()
    {
        IsZapretBusy = true;
        try
        {
            var progress = new Progress<InstallProgress>(p => ZapretMessage = p.Percent is { } pct ? $"{p.Stage} · {pct:0}%" : p.Stage);
            ZapretMessageIsError = false;
            var folder = await Task.Run(() => _zapret.InstallAsync(progress));
            SetZapretMessage($"zapret installed to {folder}.", false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
        {
            SetZapretMessage(ex.Message, true);
        }
        finally
        {
            IsZapretBusy = false;
            RefreshZapret();
        }
    }

    partial void OnZapretAutoStartChanged(bool value) => Save(s => s.ZapretAutoStart = value);
    partial void OnZapretSuggestChanged(bool value) => Save(s => s.ZapretSuggest = value);
    partial void OnSelectedZapretStrategyChanged(string? value) { if (value is not null) Save(s => s.ZapretStrategy = value); }

    private void RefreshZapret()
    {
        var status = _zapret.GetStatus();
        IsZapretInstalled = status.Installed;
        IsZapretRunning = status.Running;
        ZapretFolder = status.Folder;
        var wanted = _settings.Current.ZapretStrategy;
        _loading = true;
        ZapretStrategies.Clear();
        foreach (var s in status.Strategies) ZapretStrategies.Add(s);
        SelectedZapretStrategy = status.Strategies.FirstOrDefault(s => s.Equals(wanted, StringComparison.OrdinalIgnoreCase)) ?? status.Strategies.FirstOrDefault();
        _loading = false;
        ZapretStatusText = !status.Installed ? "Not found"
            : status.Running ? "Running"
            : status.ServiceInstalled ? "Installed as a service (stopped)"
            : "Stopped";
    }

    [RelayCommand]
    private async Task StartZapretAsync()
    {
        if (SelectedZapretStrategy is null) return;
        IsZapretBusy = true;
        try
        {
            await _zapret.StartAsync(SelectedZapretStrategy);
            SetZapretMessage($"zapret is running with {SelectedZapretStrategy}. Use “Check YouTube” to see whether it helps.", false);
        }
        catch (OperationCanceledException ex)
        {
            SetZapretMessage(ex.Message, true);
        }
        catch (InvalidOperationException ex)
        {
            SetZapretMessage(ex.Message, true);
        }
        finally
        {
            IsZapretBusy = false;
            RefreshZapret();
        }
    }

    [RelayCommand]
    private async Task StopZapretAsync()
    {
        IsZapretBusy = true;
        try
        {
            await _zapret.StopAsync();
            SetZapretMessage("zapret stopped.", false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            SetZapretMessage(ex.Message, true);
        }
        finally
        {
            IsZapretBusy = false;
            RefreshZapret();
        }
    }

    [RelayCommand]
    private async Task ChooseZapretFolderAsync()
    {
        var folder = await _windows.PickFolderAsync();
        if (folder is null) return;
        if (_zapret.ValidateFolder(folder) is { } error)
        {
            SetZapretMessage(error, true);
            return;
        }

        await SaveAsync(s => s.ZapretFolder = folder);
        ZapretMessage = null;
        RefreshZapret();
    }

    [RelayCommand]
    private void OpenZapretFolder()
    {
        if (ZapretFolder is not null) _launcher.OpenFolder(ZapretFolder);
    }

    [RelayCommand]
    private async Task CheckYouTubeAsync()
    {
        ReachabilityText = "Checking…";
        var result = await _zapret.CheckYouTubeAsync();
        ReachabilityText = (result.AllGood ? "✓ YouTube is reachable. " : "✗ YouTube looks blocked or throttled. ") + result.Details;
    }

    private void SetZapretMessage(string message, bool isError)
    {
        ZapretMessage = message;
        ZapretMessageIsError = isError;
    }

    // Cookies
    public ObservableCollection<BrowserProfile> Browsers { get; } = [];

    public IReadOnlyList<Option<CookieMode>> CookieModeOptions { get; } =
    [
        new(CookieMode.None, "Don't use cookies"),
        new(CookieMode.Imported, "Use imported cookies"),
        new(CookieMode.Browser, "Read from the browser on every download"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrowserHint), nameof(HasBrowserHint))]
    private BrowserProfile? _selectedBrowserProfile;

    [ObservableProperty] private Option<CookieMode> _selectedCookieMode = null!;
    [ObservableProperty] private bool _isImportingCookies;
    [ObservableProperty] private string _cookieStatusText = "Not used";
    [ObservableProperty] private string? _cookieDetailText;
    [ObservableProperty] private bool _hasImportedCookies;
    [ObservableProperty] private bool _hasBrowsers;
    [ObservableProperty] private string? _cookieMessage;
    [ObservableProperty] private bool _cookieMessageIsError;

    public string? BrowserHint => SelectedBrowserProfile switch
    {
        null => null,
        { IsChromium: true, IsRunning: true } p => $"{p.Browser} is running. Close it completely before importing — it locks its cookie database.",
        { IsExperimental: true } p => $"{p.Browser} isn't officially supported by yt-dlp; importing may not work.",
        { IsChromium: true } p => $"Recent versions of {p.Browser} encrypt cookies in a way yt-dlp may not be able to read. If import fails, use Firefox or a cookies.txt file.",
        _ => null,
    };

    public bool HasBrowserHint => BrowserHint is not null;

    private bool _syncingCookieMode;

    private void RefreshCookieStatus()
    {
        var status = _cookies.GetStatus();
        HasImportedCookies = status.Imported is not null;
        CookieStatusText = status.Mode switch
        {
            CookieMode.Imported when status.Imported is { } i => $"Imported · {i.Count:N0} cookies" + (i.SignedInToGoogle ? " · signed in to YouTube" : string.Empty),
            CookieMode.Browser => $"Live from {status.BrowserName}",
            _ => "Not used",
        };
        CookieDetailText = status.Imported is { } imp
            ? $"From {status.Source}, imported {Format.DateTimeShort(status.ImportedAtUtc)}. {imp.Count:N0} cookies, {imp.YouTubeCount:N0} for YouTube"
              + (imp.SignedInToGoogle ? ", including a signed-in YouTube/Google session." : ". No signed-in YouTube session was found — sign in to YouTube in that browser and import again.")
              + (imp.TopDomains.Count > 0 ? $" Top sites: {string.Join(", ", imp.TopDomains)}." : string.Empty)
            : "No cookies have been imported.";
        _syncingCookieMode = true;
        SelectedCookieMode = CookieModeOptions.First(o => o.Value == status.Mode);
        _syncingCookieMode = false;
    }

    [RelayCommand]
    private void RefreshBrowsers()
    {
        var previous = SelectedBrowserProfile?.Spec ?? _settings.Current.CookieBrowserSpec;
        Browsers.Clear();
        foreach (var b in _cookies.DetectBrowsers()) Browsers.Add(b);
        HasBrowsers = Browsers.Count > 0;
        SelectedBrowserProfile = Browsers.FirstOrDefault(b => b.Spec == previous)
                                 ?? Browsers.FirstOrDefault(b => !b.IsChromium)
                                 ?? Browsers.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ImportCookiesAsync()
    {
        if (SelectedBrowserProfile is not { } profile) return;
        IsImportingCookies = true;
        CookieMessage = null;
        try
        {
            RefreshBrowsers(); // running state may have changed
            profile = Browsers.FirstOrDefault(b => b.Spec == profile.Spec) ?? profile;
            var summary = await Task.Run(() => _cookies.ImportFromBrowserAsync(profile));
            SetCookieMessage(summary.SignedInToGoogle
                ? $"Imported {summary.Count:N0} cookies from {profile.DisplayName}. You're signed in to YouTube."
                : $"Imported {summary.Count:N0} cookies from {profile.DisplayName}, but no YouTube sign-in was found.", false);
        }
        catch (Exception ex) when (ex is CookieException or DependencyMissingException)
        {
            SetCookieMessage(ex.Message, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cookie import failed");
            SetCookieMessage("Import failed: " + ex.Message, true);
        }
        finally
        {
            IsImportingCookies = false;
            RefreshCookieStatus();
        }
    }

    [RelayCommand]
    private async Task ImportCookiesFileAsync()
    {
        var path = await _windows.PickOpenFileAsync(".txt");
        if (path is null) return;
        try
        {
            var summary = await _cookies.ImportFromFileAsync(path);
            SetCookieMessage($"Imported {summary.Count:N0} cookies from {Path.GetFileName(path)}.", false);
        }
        catch (Exception ex) when (ex is CookieException or IOException or UnauthorizedAccessException)
        {
            SetCookieMessage(ex.Message, true);
        }

        RefreshCookieStatus();
    }

    [RelayCommand]
    private async Task RemoveCookiesAsync()
    {
        if (!await _dialogs.ConfirmAsync("Remove cookies?", "Sonic will delete its imported cookies and download anonymously. Your browser isn't affected.", "Remove"))
            return;
        await _cookies.ClearAsync();
        SetCookieMessage("Cookies removed.", false);
        RefreshCookieStatus();
    }

    partial void OnSelectedCookieModeChanged(Option<CookieMode> value)
    {
        if (_syncingCookieMode || value is null) return;
        _ = ApplyCookieModeAsync(value.Value);
    }

    private async Task ApplyCookieModeAsync(CookieMode mode)
    {
        try
        {
            if (mode == CookieMode.Browser)
            {
                if (SelectedBrowserProfile is not { } profile) throw new CookieException("Choose a browser first.");
                await _cookies.UseBrowserLiveAsync(profile);
                SetCookieMessage(profile.IsChromium
                    ? $"Sonic will read {profile.Browser}'s cookies on every download. {profile.Browser} must be closed while downloading — importing once is usually more convenient."
                    : $"Sonic will read cookies from {profile.DisplayName} on every download.", false);
            }
            else
            {
                await _cookies.SetModeAsync(mode);
                CookieMessage = null;
            }
        }
        catch (CookieException ex)
        {
            SetCookieMessage(ex.Message, true);
        }

        RefreshCookieStatus();
    }

    public static Microsoft.UI.Xaml.Controls.InfoBarSeverity Severity(bool isError) =>
        isError ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;

    private void SetCookieMessage(string message, bool isError)
    {
        CookieMessage = message;
        CookieMessageIsError = isError;
    }

    [RelayCommand]
    private async Task BrowseVideoDirectoryAsync()
    {
        var folder = await _windows.PickFolderAsync();
        if (folder is null) return;
        VideoDirectory = folder;
        await SaveAsync(s => s.VideoDirectory = folder);
    }

    [RelayCommand]
    private void OpenVideoDirectory()
    {
        Directory.CreateDirectory(VideoDirectory);
        _launcher.OpenFolder(VideoDirectory);
    }

    [RelayCommand]
    private void ResetVideoFilenameTemplate() => VideoFilenameTemplate = FileNameHelper.DefaultVideoTemplate;

    // Library
    [ObservableProperty] private bool _scanOnStartup;
    [ObservableProperty] private bool _watchFolders;
    [ObservableProperty] private Option<DuplicateHandling> _selectedDuplicateHandling;

    // Advanced
    [ObservableProperty] private bool _verboseLogging;
    [ObservableProperty] private string _diagnostics = string.Empty;

    private void Load()
    {
        _loading = true;
        var s = _settings.Current;
        SelectedTheme = Options.Find(Options.Themes, s.Theme);
        LaunchAtSignIn = _startup.IsEnabled;
        SelectedStartupPage = Options.Find(Options.StartupPages, s.StartupPage);
        ResumeDownloadsOnStartup = s.ResumeDownloadsOnStartup;
        ShowNotifications = s.ShowNotifications;
        ConfirmExit = s.ConfirmExitWithActiveDownloads;
        DownloadDirectory = s.DownloadDirectory;
        FilenameTemplate = s.FilenameTemplate;
        MaxConcurrentDownloads = s.MaxConcurrentDownloads;
        SelectedFormat = Options.Find(Options.Formats, s.DefaultFormat);
        SelectedQuality = Options.Find(Options.Qualities, s.DefaultQuality);
        EmbedArtwork = s.EmbedArtwork;
        CropArtwork = s.CropArtworkToSquare;
        FindAlbumArt = s.FindAlbumArt;
        CleanTitles = s.CleanTitles;
        MaxRetries = s.MaxRetries;
        SpotifyClientId = s.SpotifyClientId ?? string.Empty;
        LikedSync = s.SpotifyLikedSync;
        LikedModeIndex = s.SpotifyLikedSync && s.SpotifyLikedSyncFrom is null ? 1 : 0;
        LikedFolder = s.SpotifyLikedSyncFolder ?? s.DownloadDirectory;
        SpotifyClientSecret = _settings.GetSpotifyClientSecret() ?? string.Empty;
        IsSpotifyConfigured = _spotify.IsConfigured;
        CheckForToolUpdates = s.CheckForToolUpdates;
        YtDlpOverride = s.YtDlpPathOverride;
        FfmpegOverride = s.FfmpegPathOverride;
        VideoDirectory = s.VideoDirectory;
        VideoFilenameTemplate = s.VideoFilenameTemplate;
        SelectedVideoContainer = Options.Find(Options.Containers, s.DefaultVideoContainer);
        SelectedVideoQuality = Options.PickHeight(Options.StandardVideoQualities, s.DefaultVideoMaxHeight);
        SelectedVideoCodec = Options.Find(Options.VideoCodecs, s.DefaultVideoCodec);
        SelectedSubtitleMode = Options.Find(Options.SubtitleModes, s.DefaultSubtitleMode);
        SelectedSubtitleFormat = Options.Find(Options.SubtitleFormats, s.DefaultSubtitleFormat);
        SubtitleLanguages = s.DefaultSubtitleLanguages;
        IncludeAutoSubtitles = s.IncludeAutoSubtitles;
        EmbedVideoThumbnail = s.EmbedVideoThumbnail;
        EmbedVideoChapters = s.EmbedVideoChapters;
        SpeedLimitKBps = s.SpeedLimitKBps;
        ProxyUrl = s.ProxyUrl ?? string.Empty;
        SponsorBlockAudio = s.SponsorBlockAudio;
        SelectedSponsorBlockVideo = SponsorBlockModes.First(o => o.Value == s.SponsorBlockVideo);
        WatchClipboard = s.WatchClipboard;
        DiscordPresence = s.DiscordPresence;
        AutoCheckUpdates = s.AutoCheckUpdates;
        UpdateFeed = s.UpdateFeed ?? string.Empty;
        DiscordClientId = s.DiscordClientId ?? string.Empty;
        PauseWhileGaming = s.PauseWhileGaming;
        LoadIgnoredApps();
        AutoRetryOnReconnect = s.AutoRetryOnReconnect;
        WarnAlreadyDownloaded = s.WarnAlreadyDownloaded;
        Presets.Clear();
        foreach (var preset in s.Presets) Presets.Add(preset);
        MinimizeToTray = s.MinimizeToTray;
        GlobalHotkey = s.GlobalHotkey;
        ProtocolHandler = s.ProtocolHandler;
        ZapretAutoStart = s.ZapretAutoStart;
        ZapretAutoInstall = s.ZapretAutoInstall;
        AutoInstallTools = s.AutoInstallTools;
        ZapretSuggest = s.ZapretSuggest;
        ScanOnStartup = s.ScanOnStartup;
        WatchFolders = s.WatchFolders;
        SelectedDuplicateHandling = Options.Find(Options.DuplicateHandlings, s.DuplicateHandling);
        VerboseLogging = s.VerboseLogging;
        MusicFolders.Clear();
        foreach (var f in s.MusicFolders) MusicFolders.Add(f);
        UpdateFilenamePreview();
        _loading = false;
    }

    public Task OnNavigatedToAsync(object? parameter)
    {
        Load();
        RefreshBrowsers();
        RefreshCookieStatus();
        RefreshZapret();
        UpdateHotkeyStatus();
        FocusSection = parameter as string;
        BuildDiagnostics();
        return Task.CompletedTask;
    }

    public void OnNavigatedFrom() { }

    private void Save(Action<AppSettings> update)
    {
        if (_loading) return;
        _ = SaveAsync(update);
    }

    private async Task SaveAsync(Action<AppSettings> update)
    {
        try
        {
            await _settings.UpdateAsync(update);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving settings failed");
            _notifications.ShowError("Couldn't save settings", ex.Message);
        }
    }

    partial void OnSelectedThemeChanged(Option<AppTheme> value)
    {
        if (_loading || value is null) return;
        _theme.Apply(value.Value);
        Save(s => s.Theme = value.Value);
    }

    partial void OnLaunchAtSignInChanged(bool value)
    {
        if (_loading) return;
        try
        {
            _startup.SetEnabled(value);
            Save(s => s.LaunchAtSignIn = value);
        }
        catch (InvalidOperationException ex)
        {
            _notifications.ShowError("Startup setting", ex.Message);
            _loading = true;
            LaunchAtSignIn = !value;
            _loading = false;
        }
    }

    partial void OnSelectedStartupPageChanged(Option<StartupPage> value) { if (value is not null) Save(s => s.StartupPage = value.Value); }
    partial void OnResumeDownloadsOnStartupChanged(bool value) => Save(s => s.ResumeDownloadsOnStartup = value);
    partial void OnShowNotificationsChanged(bool value) => Save(s => s.ShowNotifications = value);
    partial void OnConfirmExitChanged(bool value) => Save(s => s.ConfirmExitWithActiveDownloads = value);

    partial void OnFilenameTemplateChanged(string value)
    {
        FilenameTemplateError = FileNameHelper.ValidateTemplate(value);
        UpdateFilenamePreview();
        if (FilenameTemplateError is null) Save(s => s.FilenameTemplate = value.Trim());
    }

    private void UpdateFilenamePreview()
    {
        var sample = new TrackMetadata { Title = "Get Lucky", Artist = "Daft Punk", Album = "Random Access Memories", AlbumArtist = "Daft Punk", TrackNumber = 8, Year = 2013, Genre = "Electronic" };
        var template = FileNameHelper.ValidateTemplate(FilenameTemplate) is null ? FilenameTemplate : FileNameHelper.DefaultTemplate;
        FilenamePreview = FileNameHelper.ExpandTemplate(template, sample, "Daft Punk", "5NV6Rdv1a3I") + (SelectedFormat?.Value is AudioFormat.Original or null ? ".m4a" : FileNameHelper.GetExtension(SelectedFormat.Value));
    }

    partial void OnMaxConcurrentDownloadsChanged(double value)
    {
        if (double.IsNaN(value)) return;
        Save(s => s.MaxConcurrentDownloads = (int)Math.Round(value));
    }

    partial void OnSelectedFormatChanged(Option<AudioFormat> value)
    {
        if (value is null) return;
        UpdateFilenamePreview();
        Save(s => s.DefaultFormat = value.Value);
    }

    partial void OnSelectedQualityChanged(Option<AudioQuality> value) { if (value is not null) Save(s => s.DefaultQuality = value.Value); }
    partial void OnEmbedArtworkChanged(bool value) => Save(s => s.EmbedArtwork = value);
    partial void OnCropArtworkChanged(bool value) => Save(s => s.CropArtworkToSquare = value);

    [ObservableProperty] private bool _findAlbumArt;

    partial void OnFindAlbumArtChanged(bool value) => Save(s => s.FindAlbumArt = value);
    partial void OnCleanTitlesChanged(bool value) => Save(s => s.CleanTitles = value);

    partial void OnMaxRetriesChanged(double value)
    {
        if (double.IsNaN(value)) return;
        Save(s => s.MaxRetries = (int)Math.Round(value));
    }

    partial void OnCheckForToolUpdatesChanged(bool value) => Save(s => s.CheckForToolUpdates = value);

    partial void OnScanOnStartupChanged(bool value) => Save(s => s.ScanOnStartup = value);

    partial void OnWatchFoldersChanged(bool value)
    {
        if (_loading) return;
        _ = SaveAsync(s => s.WatchFolders = value).ContinueWith(_ => _library.ConfigureWatchers(), TaskScheduler.Default);
    }

    partial void OnSelectedDuplicateHandlingChanged(Option<DuplicateHandling> value) { if (value is not null) Save(s => s.DuplicateHandling = value.Value); }

    partial void OnVerboseLoggingChanged(bool value)
    {
        _levelSwitch.MinimumLevel = value ? LogEventLevel.Debug : LogEventLevel.Information;
        Save(s => s.VerboseLogging = value);
    }

    [RelayCommand]
    private async Task BrowseDownloadDirectoryAsync()
    {
        var folder = await _windows.PickFolderAsync();
        if (folder is null) return;
        DownloadDirectory = folder;
        await SaveAsync(s => s.DownloadDirectory = folder);
        _library.ConfigureWatchers();
    }

    [RelayCommand]
    private void OpenDownloadDirectory()
    {
        Directory.CreateDirectory(DownloadDirectory);
        _launcher.OpenFolder(DownloadDirectory);
    }

    [RelayCommand]
    private void ResetFilenameTemplate() => FilenameTemplate = FileNameHelper.DefaultTemplate;

    [RelayCommand]
    private void InsertToken(string token) => FilenameTemplate = (FilenameTemplate ?? string.Empty) + token;

    [RelayCommand]
    private async Task SaveSpotifyAsync()
    {
        IsTestingSpotify = true;
        SpotifyStatus = null;
        try
        {
            await _settings.SetSpotifyCredentialsAsync(SpotifyClientId, SpotifyClientSecret);
            IsSpotifyConfigured = _spotify.IsConfigured;
            if (!IsSpotifyConfigured)
            {
                SpotifyStatus = "Spotify credentials removed.";
                return;
            }

            await _spotify.VerifyCredentialsAsync();
            SpotifyStatus = "Connected. Credentials verified with Spotify.";
        }
        catch (SpotifyException ex)
        {
            SpotifyStatus = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving Spotify credentials failed");
            SpotifyStatus = "Couldn't save the credentials: " + ex.Message;
        }
        finally
        {
            IsTestingSpotify = false;
        }
    }

    [RelayCommand]
    private void OpenSpotifyDashboard() => _launcher.OpenUri("https://developer.spotify.com/dashboard");

    [RelayCommand]
    private async Task CheckToolsAsync()
    {
        IsCheckingTools = true;
        try
        {
            await _dependencies.RefreshAsync(checkForUpdates: true);
            BuildDiagnostics();
        }
        finally
        {
            IsCheckingTools = false;
        }
    }

    [RelayCommand]
    private async Task ChooseYtDlpAsync()
    {
        var folder = await _windows.PickFolderAsync();
        if (folder is null) return;
        var exe = Path.Combine(folder, "yt-dlp.exe");
        if (!File.Exists(exe))
        {
            await _dialogs.ShowErrorAsync("yt-dlp not found", $"The folder \"{folder}\" doesn't contain yt-dlp.exe.");
            return;
        }

        YtDlpOverride = exe;
        await SaveAsync(s => s.YtDlpPathOverride = exe);
        await CheckToolsAsync();
    }

    [RelayCommand]
    private async Task ChooseFfmpegAsync()
    {
        var folder = await _windows.PickFolderAsync();
        if (folder is null) return;
        var exe = Path.Combine(folder, "ffmpeg.exe");
        if (!File.Exists(exe))
        {
            await _dialogs.ShowErrorAsync("FFmpeg not found", $"The folder \"{folder}\" doesn't contain ffmpeg.exe.");
            return;
        }

        FfmpegOverride = exe;
        await SaveAsync(s => s.FfmpegPathOverride = exe);
        await CheckToolsAsync();
    }

    [RelayCommand]
    private async Task ResetToolOverridesAsync()
    {
        YtDlpOverride = null;
        FfmpegOverride = null;
        await SaveAsync(s =>
        {
            s.YtDlpPathOverride = null;
            s.FfmpegPathOverride = null;
        });
        await CheckToolsAsync();
    }

    [RelayCommand]
    private async Task AddMusicFolderAsync()
    {
        var folder = await _windows.PickFolderAsync();
        if (folder is null || MusicFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
        MusicFolders.Add(folder);
        await SaveAsync(s => s.MusicFolders.Add(folder));
        _library.ConfigureWatchers();
        await RescanAsync();
    }

    [RelayCommand]
    private async Task RemoveMusicFolderAsync(string folder)
    {
        if (!await _dialogs.ConfirmAsync("Remove folder?", $"Songs in \"{folder}\" will be removed from the library. The files themselves are not deleted.", "Remove"))
            return;
        MusicFolders.Remove(folder);
        await SaveAsync(s => s.MusicFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)));
        _library.ConfigureWatchers();
        await _library.RemoveFolderAsync(folder);
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (_library.IsScanning) return;
        _notifications.ShowInfo("Scanning library", "Looking for new and changed music…");
        try
        {
            var result = await Task.Run(() => _library.ScanAsync(null));
            _notifications.ShowSuccess("Library updated", $"{result.Added} added, {result.Updated} updated, {result.Removed} removed.");
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Scan failed", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenLogs() => _launcher.OpenFolder(_paths.LogsDirectory);

    [RelayCommand]
    private void OpenDataFolder() => _launcher.OpenFolder(_paths.DataDirectory);

    [RelayCommand]
    private void CopyDiagnostics()
    {
        BuildDiagnostics();
        var package = new DataPackage();
        package.SetText(Diagnostics);
        Clipboard.SetContent(package);
        _notifications.ShowInfo("Copied", "Diagnostic information was copied to the clipboard.");
    }

    private void BuildDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Sonic {AppVersion.Replace("Version ", string.Empty)}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($".NET {Environment.Version} · Windows App SDK {typeof(Microsoft.UI.Xaml.Application).Assembly.GetName().Version}");
        foreach (var tool in Tools.All)
            sb.AppendLine($"{tool.DisplayName}: {tool.StatusText}{(tool.Path is null ? string.Empty : $" ({tool.Path})")}");
        sb.AppendLine($"Data folder: {_paths.DataDirectory}");
        try
        {
            var db = new FileInfo(_paths.DatabasePath);
            if (db.Exists) sb.AppendLine($"Database: {Format.Bytes(db.Length)}");
        }
        catch (IOException)
        {
        }

        sb.AppendLine($"Download folder: {_settings.Current.DownloadDirectory}");
        sb.Append($"Video folder: {_settings.Current.VideoDirectory}");
        Diagnostics = sb.ToString();
    }
}
