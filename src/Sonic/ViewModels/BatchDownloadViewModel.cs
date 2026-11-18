using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;
using Sonic.Helpers;
using Sonic.Services;

namespace Sonic.ViewModels;

public enum BatchItemState
{
    Ready,
    NotSearched,
    Searching,
    Found,
    NotFound,
    Error,
}

public sealed partial class BatchItemViewModel : ObservableObject
{
    private int _alternativeIndex;

    public BatchItemViewModel(BatchEntry entry, int index)
    {
        Entry = entry;
        Index = index;
        _state = entry.IsLink ? BatchItemState.Ready : BatchItemState.NotSearched;
    }

    public BatchEntry Entry { get; }
    public int Index { get; }
    public string Input => Entry.Display;
    public IReadOnlyList<MediaInfo> Alternatives { get; private set; } = [];

    [ObservableProperty] private bool _isSelected = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchTitle), nameof(MatchDetails), nameof(Thumbnail), nameof(StatusGlyph), nameof(IsSearching), nameof(CanCycle), nameof(IsReady))]
    private BatchItemState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchTitle), nameof(MatchDetails), nameof(Thumbnail), nameof(IsReady))]
    private MediaInfo? _match;

    [ObservableProperty] private string? _error;

    /// <summary>Found in the download history; such items start deselected.</summary>
    [ObservableProperty] private bool _isDownloaded;

    /// <summary>The URL last looked up in the history (avoids repeated lookups).</summary>
    internal string? CheckedUrl { get; set; }

    public bool IsSearching => State == BatchItemState.Searching;
    public bool IsReady => State == BatchItemState.Ready || (State == BatchItemState.Found && Match is not null);
    public bool CanCycle => State == BatchItemState.Found && Alternatives.Count > 1;
    public string? Url => Entry.Url ?? Match?.Url;
    public string? Thumbnail => Match?.ThumbnailUrl;

    public string MatchTitle => State switch
    {
        BatchItemState.Ready => "Direct link",
        BatchItemState.NotSearched => "Not searched yet",
        BatchItemState.Searching => "Searching…",
        BatchItemState.Found => Match?.Title ?? "Found",
        BatchItemState.NotFound => "Nothing found",
        _ => "Search failed",
    };

    public string MatchDetails
    {
        get
        {
            if (State == BatchItemState.Error) return Error ?? string.Empty;
            if (State == BatchItemState.Ready) return SourceDetector.GetDisplayName(SourceDetector.Detect(Entry.Url));
            if (Match is not { } m) return string.Empty;
            var parts = new List<string?> { m.Uploader, m.DurationSeconds is > 0 ? Format.Duration(m.DurationSeconds) : null };
            if (Entry.DurationSeconds is > 0 && m.DurationSeconds is > 0)
            {
                var diff = Math.Abs(m.DurationSeconds.Value - Entry.DurationSeconds.Value);
                parts.Add(diff <= 3 ? "length matches" : $"{diff:0}s longer/shorter");
            }

            if (Alternatives.Count > 1) parts.Add($"match {_alternativeIndex + 1} of {Alternatives.Count}");
            return string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
        }
    }

    public string StatusGlyph => State switch
    {
        BatchItemState.Ready or BatchItemState.Found => Glyphs.Completed,
        BatchItemState.NotFound or BatchItemState.Error => Glyphs.Warning,
        BatchItemState.Searching => Glyphs.Search,
        _ => Glyphs.Clock,
    };

    public void SetResults(IReadOnlyList<MediaInfo> results)
    {
        // Prefer the result whose length matches the list (when known), otherwise the top search hit.
        var target = Entry.DurationSeconds;
        var ordered = target is > 0
            ? results.OrderBy(r => r.DurationSeconds is > 0 ? Math.Abs(r.DurationSeconds.Value - target.Value) <= 15 ? 0 : 1 : 2)
                .ThenBy(r => r.DurationSeconds is > 0 ? Math.Abs(r.DurationSeconds.Value - target.Value) : double.MaxValue).ToList()
            : results.ToList();
        Alternatives = ordered;
        _alternativeIndex = 0;
        Match = ordered.FirstOrDefault();
        State = Match is null ? BatchItemState.NotFound : BatchItemState.Found;
        OnPropertyChanged(nameof(CanCycle));
        OnPropertyChanged(nameof(MatchDetails));
    }

    [RelayCommand]
    private void NextMatch()
    {
        if (Alternatives.Count < 2) return;
        _alternativeIndex = (_alternativeIndex + 1) % Alternatives.Count;
        Match = Alternatives[_alternativeIndex];
        OnPropertyChanged(nameof(MatchDetails));
    }
}

/// <summary>
/// Downloads a list of links and/or "Artist - Title" queries (pasted, or loaded from .txt/.csv/.m3u).
/// Queries are searched on YouTube and shown for review before anything is downloaded.
/// </summary>
public sealed partial class BatchDownloadViewModel : ObservableObject
{
    private const int SearchParallelism = 3;
    private readonly IYtDlpService ytDlp;
    private readonly IDownloadQueueService queue;
    private readonly IPlaylistService playlists;
    private readonly ISettingsService settings;
    private readonly IWindowService windows;
    private readonly IUiDispatcher dispatcher;
    private readonly ILogger<BatchDownloadViewModel> logger;

    public BatchDownloadViewModel(
        IYtDlpService ytDlp,
        IDownloadQueueService queue,
        IPlaylistService playlists,
        ISettingsService settings,
        IWindowService windows,
        IUiDispatcher dispatcher,
        ILogger<BatchDownloadViewModel> logger)
    {
        this.ytDlp = ytDlp;
        this.queue = queue;
        this.playlists = playlists;
        this.settings = settings;
        this.windows = windows;
        this.dispatcher = dispatcher;
        this.logger = logger;
        var s = settings.Current;
        _modeIndex = s.LastMediaKind == MediaKind.Video ? 1 : 0;
        _selectedFormat = Options.Find(Options.Formats, s.DefaultFormat);
        _selectedVideoQuality = Options.PickHeight(Options.StandardVideoQualities, s.DefaultVideoMaxHeight);
        _recentFolders = s.RecentFolders.ToList();
        ApplyDefaultFolder();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderError), nameof(HasFolderError))]
    private string _saveToFolder = string.Empty;

    [ObservableProperty] private string _defaultFolder = string.Empty;
    [ObservableProperty] private IReadOnlyList<string> _recentFolders;

    public string? FolderError => FileNameHelper.ValidateDirectory(SaveToFolder);
    public bool HasFolderError => FolderError is not null;

    private void ApplyDefaultFolder()
    {
        var newDefault = IsVideoMode ? settings.Current.VideoDirectory : settings.Current.DownloadDirectory;
        if (string.IsNullOrWhiteSpace(SaveToFolder) || string.Equals(SaveToFolder, DefaultFolder, StringComparison.OrdinalIgnoreCase))
            SaveToFolder = newDefault;
        DefaultFolder = newDefault;
    }

    partial void OnModeIndexChanged(int value) => ApplyDefaultFolder();

    private IReadOnlyList<BatchEntry>? _fileEntries;
    private bool _settingTextFromFile;
    private CancellationTokenSource? _parseDebounce;
    private CancellationTokenSource? _searchCts;

    public ObservableCollection<BatchItemViewModel> Items { get; } = [];
    public IReadOnlyList<Option<AudioFormat>> FormatOptions => Options.Formats;
    public IReadOnlyList<Option<int>> VideoQualityOptions => Options.StandardVideoQualities;

    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private string? _fileName;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _summary = "Paste links or song names, one per line — or open a .txt, .csv or .m3u file.";
    [ObservableProperty] private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVideoMode), nameof(IsAudioMode), nameof(CanSaveAsPlaylist))]
    private int _modeIndex;

    [ObservableProperty] private Option<AudioFormat> _selectedFormat;
    [ObservableProperty] private Option<int> _selectedVideoQuality;
    [ObservableProperty] private bool _saveAsPlaylist;
    [ObservableProperty] private string _playlistName = $"Batch {DateTime.Now:d MMM yyyy}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadButtonText), nameof(CanDownload))]
    private int _readyCount;

    [ObservableProperty] private int _unsearchedCount;

    public bool IsVideoMode => ModeIndex == 1;
    public bool IsAudioMode => !IsVideoMode;
    public bool CanSaveAsPlaylist => IsAudioMode;
    public bool CanDownload => ReadyCount > 0 && !IsSearching;
    public string DownloadButtonText => ReadyCount > 0 ? $"Download {ReadyCount}" : "Download";
    public int QueuedCount { get; private set; }

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(CanDownload));

    partial void OnInputTextChanged(string value)
    {
        if (!_settingTextFromFile)
        {
            _fileEntries = null; // edited by hand: parse the text instead of the file's columns
            FileName = null;
        }

        _parseDebounce?.Cancel();
        _parseDebounce = new CancellationTokenSource();
        var token = _parseDebounce.Token;
        _ = Task.Delay(350, token).ContinueWith(t => { if (!t.IsCanceled) dispatcher.Post(Rebuild); }, TaskScheduler.Default);
    }

    public void LoadText(string text)
    {
        InputText = text;
        Rebuild();
    }

    public async Task LoadFileAsync(string path)
    {
        try
        {
            var entries = await Task.Run(() => BatchListParser.ParseFile(path));
            _fileEntries = entries;
            _settingTextFromFile = true;
            InputText = string.Join(Environment.NewLine, entries.Select(e => e.Url ?? e.Display));
            _settingTextFromFile = false;
            FileName = Path.GetFileName(path);
            if (Path.GetFileNameWithoutExtension(path) is { Length: > 0 } name) PlaylistName = name;
            Rebuild();
            Error = entries.Count == 0 ? "No songs or links were found in that file." : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "The file couldn't be read: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var path = await windows.PickOpenFileAsync(".txt", ".csv", ".m3u", ".m3u8");
        if (path is not null) await LoadFileAsync(path);
    }

    private void Rebuild()
    {
        var entries = _fileEntries ?? BatchListParser.ParseLines(InputText);
        // Keep results that were already searched when the same line is still present.
        var previous = Items.ToDictionary(i => (i.Entry.Url ?? i.Entry.Query).ToLowerInvariant(), i => i);
        Items.Clear();
        var index = 1;
        foreach (var entry in entries)
        {
            var key = (entry.Url ?? entry.Query).ToLowerInvariant();
            var item = previous.TryGetValue(key, out var old) && old.Entry == entry ? old : new BatchItemViewModel(entry, index);
            item.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(BatchItemViewModel.IsSelected) or nameof(BatchItemViewModel.State)) UpdateCounts(); };
            Items.Add(item);
            index++;
        }

        UpdateCounts();
        _ = MarkDownloadedAsync(Items.ToList());
    }

    /// <summary>Flags (and deselects) items that were downloaded before.</summary>
    private async Task MarkDownloadedAsync(IReadOnlyList<BatchItemViewModel> items)
    {
        if (!settings.Current.WarnAlreadyDownloaded) return;
        var changed = false;
        foreach (var item in items)
        {
            if (item.Url is not { } url || item.CheckedUrl == url) continue;
            item.CheckedUrl = url;
            try
            {
                var found = await queue.FindPreviousDownloadAsync(url) is { FilePath: { } file } && File.Exists(file);
                item.IsDownloaded = found;
                if (found && item.IsSelected)
                {
                    item.IsSelected = false;
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "History lookup failed");
                return;
            }
        }

        if (changed)
        {
            var count = Items.Count(i => i.IsDownloaded);
            Summary += $" · {Format.Count(count, "item")} already downloaded (deselected)";
        }
    }

    private void UpdateCounts()
    {
        ReadyCount = Items.Count(i => i.IsSelected && i.IsReady);
        UnsearchedCount = Items.Count(i => i.IsSelected && i.State == BatchItemState.NotSearched);
        var links = Items.Count(i => i.Entry.IsLink);
        Summary = Items.Count == 0
            ? "Paste links or song names, one per line — or open a .txt, .csv or .m3u file."
            : $"{Format.Count(Items.Count, "item")}: {links} links, {Items.Count - links} to search" +
              (UnsearchedCount > 0 ? $" · {UnsearchedCount} not searched yet — select Find matches" : string.Empty);
    }

    [RelayCommand]
    private async Task FindMatchesAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        var pending = Items.Where(i => i.IsSelected && i.State is BatchItemState.NotSearched or BatchItemState.Error or BatchItemState.NotFound).ToList();
        if (pending.Count == 0) return;

        IsSearching = true;
        Error = null;
        var suffix = IsVideoMode ? string.Empty : " audio";
        using var gate = new SemaphoreSlim(SearchParallelism);
        try
        {
            await Task.WhenAll(pending.Select(async item =>
            {
                await gate.WaitAsync(token);
                try
                {
                    dispatcher.Post(() => item.State = BatchItemState.Searching);
                    var results = await ytDlp.SearchAsync(item.Entry.Query + suffix, 3, token);
                    dispatcher.Post(() => item.SetResults(results));
                }
                catch (OperationCanceledException)
                {
                    dispatcher.Post(() => item.State = BatchItemState.NotSearched);
                }
                catch (Exception ex) when (ex is YtDlpException or DependencyMissingException)
                {
                    dispatcher.Post(() =>
                    {
                        item.Error = ex.Message;
                        item.State = BatchItemState.Error;
                    });
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsSearching = false;
            UpdateCounts();
            _ = MarkDownloadedAsync(Items.ToList());
            var failed = Items.Count(i => i.State == BatchItemState.Error);
            if (failed > 0) Error = $"{failed} searches failed. {Items.First(i => i.State == BatchItemState.Error).Error}";
        }
    }

    [RelayCommand]
    private void StopSearching() => _searchCts?.Cancel();

    [RelayCommand]
    private void SelectAll(bool select)
    {
        foreach (var i in Items) i.IsSelected = select;
    }

    /// <summary>Queues every selected item that has a link or a confirmed match. Returns false to keep the dialog open.</summary>
    public async Task<bool> DownloadAsync()
    {
        var ready = Items.Where(i => i.IsSelected && i.IsReady && i.Url is not null).ToList();
        if (ready.Count == 0) return false;
        if (FolderError is { } folderError)
        {
            Error = "Check the folder: " + folderError;
            return false;
        }

        Error = null;
        var kind = IsVideoMode ? MediaKind.Video : MediaKind.Audio;

        try
        {
            IReadOnlyList<PlaylistEntry>? entries = null;
            if (SaveAsPlaylist && kind == MediaKind.Audio)
            {
                var playlist = await playlists.CreateAsync(string.IsNullOrWhiteSpace(PlaylistName) ? "Batch" : PlaylistName);
                entries = await playlists.AddExternalEntriesAsync(playlist.Id, ready.Select(i => new PlaylistEntry
                {
                    Title = i.Entry.Title ?? i.Match?.Track ?? i.Match?.Title ?? i.Entry.Display,
                    Artist = i.Entry.Artist ?? i.Match?.Artist ?? i.Match?.Uploader,
                    Album = i.Entry.Album,
                    DurationSeconds = i.Entry.DurationSeconds ?? i.Match?.DurationSeconds ?? 0,
                    ArtworkUrl = i.Match?.ThumbnailUrl,
                }).ToList());
            }

            var video = kind == MediaKind.Video ? settings.Current.CreateDefaultVideoOptions() : null;
            if (video is not null) video.MaxHeight = SelectedVideoQuality.Value;

            for (var n = 0; n < ready.Count; n++)
            {
                var item = ready[n];
                // Song lists know the real artist/title; use them for the tags instead of the video title.
                var hints = kind == MediaKind.Audio && item.Entry is { Title: { Length: > 0 }, Artist: { Length: > 0 } } e
                    ? new MetadataHints { Title = e.Title, Artist = e.Artist, Album = e.Album }
                    : null;
                await queue.EnqueueAsync(new DownloadRequest
                {
                    Url = item.Url!,
                    Kind = kind,
                    OutputDirectory = SaveToFolder.Trim(),
                    Video = video,
                    Title = item.Entry.Title ?? item.Match?.Title,
                    Artist = item.Entry.Artist ?? item.Match?.Uploader,
                    ThumbnailUrl = item.Match?.ThumbnailUrl,
                    DurationSeconds = item.Match?.DurationSeconds,
                    Format = SelectedFormat.Value,
                    Hints = hints,
                    PlaylistEntryId = entries?[n].Id,
                });
                QueuedCount++;
            }

            await settings.UpdateAsync(s => s.LastMediaKind = kind);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Batch enqueue failed");
            Error = ex.Message;
            return QueuedCount > 0;
        }
    }

    public void Cancel()
    {
        _searchCts?.Cancel();
        _parseDebounce?.Cancel();
    }
}
