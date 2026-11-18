using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Sonic.Core.Models;
using Sonic.Helpers;
using Sonic.Services;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Sonic.ViewModels;

public enum RepeatMode
{
    Off,
    All,
    One,
}

/// <summary>
/// The built-in audio player. Uses Windows' MediaPlayer, which also wires up media keys and the system
/// media overlay (System Media Transport Controls).
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly MediaPlayer _player = new() { AudioCategory = MediaPlayerAudioCategory.Media, AutoPlay = false };
    private readonly IUiDispatcher _dispatcher;
    private readonly IInAppNotificationService _notifications;
    private readonly IShellLauncher _launcher;
    private readonly ILogger<PlayerViewModel> _logger;
    private readonly DispatcherQueueTimer _timer;
    private readonly Stack<int> _shuffleHistory = new();
    private readonly Random _random = new();
    private bool _updatingPosition;
    private int _loadVersion;

    public PlayerViewModel(IUiDispatcher dispatcher, IInAppNotificationService notifications, IShellLauncher launcher, ILogger<PlayerViewModel> logger)
    {
        _dispatcher = dispatcher;
        _notifications = notifications;
        _launcher = launcher;
        _logger = logger;

        _player.CommandManager.IsEnabled = true;
        _player.CommandManager.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        _player.CommandManager.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        _player.CommandManager.NextReceived += (sender, e) => { e.Handled = true; dispatcher.Post(() => _ = NextCoreAsync(userInitiated: true)); };
        _player.CommandManager.PreviousReceived += (sender, e) => { e.Handled = true; dispatcher.Post(() => _ = PreviousAsync()); };
        _player.MediaEnded += (_, _) => dispatcher.Post(() => _ = NextCoreAsync(userInitiated: false));
        _player.MediaFailed += (_, e) => dispatcher.Post(() => OnFailed(e.ErrorMessage));
        _player.PlaybackSession.PlaybackStateChanged += (s, _) => dispatcher.Post(() => IsPlaying = s.PlaybackState == MediaPlaybackState.Playing);

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => UpdatePosition();
        _timer.Start();
        _volume = 80;
        _player.Volume = 0.8;
    }

    public ObservableCollection<Track> Queue { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrack), nameof(Title), nameof(Artist), nameof(Artwork))]
    private Track? _current;

    [ObservableProperty] private int _currentIndex = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseGlyph), nameof(PlayPauseLabel))]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    private double _position;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private double _duration = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeGlyph))]
    private double _volume;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeGlyph))]
    private bool _isMuted;

    [ObservableProperty] private bool _shuffle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatGlyph), nameof(IsRepeatOn), nameof(RepeatLabel))]
    private RepeatMode _repeat;

    public bool HasTrack => Current is not null;
    public string Title => Current?.Title ?? string.Empty;
    public string Artist => Current is null ? string.Empty
        : IsPreview(Current) ? $"Preview · {Current.DisplayArtist}"
        : $"{Current.DisplayArtist}{(string.IsNullOrWhiteSpace(Current.Album) ? string.Empty : " · " + Current.Album)}";

    /// <summary>Preview tracks stream from the web: their "file path" is the page URL.</summary>
    public static bool IsPreview(Track track) =>
        track.FilePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || track.FilePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Streams a web video's audio without downloading it.</summary>
    public Task PreviewAsync(MediaInfo info)
    {
        var track = new Track
        {
            Id = -Math.Abs(info.Url.GetHashCode()),
            FilePath = info.Url,
            SourceUrl = info.Url,
            Title = info.Track ?? info.Title,
            Artist = info.DisplayArtist,
            ArtworkPath = info.ThumbnailUrl,
            DurationSeconds = info.DurationSeconds ?? 0,
        };
        _notifications.ShowInfo("Preview", $"Loading \"{track.Title}\"…");
        return PlayAsync([track]);
    }
    public string? Artwork => Current?.ArtworkPath;
    public string PositionText => Format.Duration(Position < 1 ? 0.0001 : Position).Replace("–", "0:00");
    public string DurationText => Format.Duration(Duration);
    public string PlayPauseGlyph => IsPlaying ? Glyphs.Pause : Glyphs.Play;
    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";
    public string VolumeGlyph => IsMuted || Volume <= 0 ? "" : Volume < 34 ? "" : Volume < 67 ? "" : "";
    public string RepeatGlyph => Repeat == RepeatMode.One ? "" : "";
    public bool IsRepeatOn => Repeat != RepeatMode.Off;
    public string RepeatLabel => Repeat switch
    {
        RepeatMode.All => "Repeat all",
        RepeatMode.One => "Repeat one",
        _ => "Repeat off",
    };

    partial void OnVolumeChanged(double value)
    {
        _player.Volume = Math.Clamp(value / 100.0, 0, 1);
        if (value > 0 && IsMuted) IsMuted = false;
    }

    partial void OnIsMutedChanged(bool value) => _player.IsMuted = value;

    /// <summary>Called by the seek slider (two-way bound). Ignores updates that come from the timer.</summary>
    partial void OnPositionChanged(double value)
    {
        if (_updatingPosition || Current is null) return;
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(value);
    }

    /// <summary>Replaces the queue and starts playing at <paramref name="startIndex"/>.</summary>
    public async Task PlayAsync(IEnumerable<Track> tracks, int startIndex = 0)
    {
        var list = tracks.ToList();
        if (list.Count == 0) return;
        Queue.Clear();
        foreach (var t in list) Queue.Add(t);
        _shuffleHistory.Clear();
        await LoadAsync(Math.Clamp(startIndex, 0, list.Count - 1), autoPlay: true);
    }

    public async Task PlayTrackAsync(Track track, IReadOnlyList<Track>? context = null)
    {
        var list = context ?? [track];
        var index = list.ToList().FindIndex(t => t.Id == track.Id && t.FilePath == track.FilePath);
        await PlayAsync(list, Math.Max(0, index));
    }

    public void AddToQueue(IEnumerable<Track> tracks)
    {
        var wasEmpty = Queue.Count == 0;
        foreach (var t in tracks) Queue.Add(t);
        if (wasEmpty && Queue.Count > 0) _ = LoadAsync(0, autoPlay: true);
        else _notifications.ShowInfo("Added to queue", Format.Count(tracks.Count(), "song"));
    }

    private async Task LoadAsync(int index, bool autoPlay)
    {
        if (index < 0 || index >= Queue.Count) return;
        var version = ++_loadVersion;
        var track = Queue[index];
        CurrentIndex = index;
        Current = track;
        Position = 0;
        Duration = Math.Max(1, track.DurationSeconds);
        try
        {
            MediaSource source;
            if (IsPreview(track))
            {
                var ytDlp = App.GetService<Sonic.Core.Abstractions.IYtDlpService>();
                var stream = await Task.Run(() => ytDlp.GetStreamUrlAsync(track.FilePath));
                if (version != _loadVersion) return;
                source = MediaSource.CreateFromUri(new Uri(stream));
            }
            else
            {
                var file = await StorageFile.GetFileFromPathAsync(track.FilePath);
                if (version != _loadVersion) return;
                source = MediaSource.CreateFromStorageFile(file);
            }

            var item = new MediaPlaybackItem(source);
            var props = item.GetDisplayProperties();
            props.Type = MediaPlaybackType.Music;
            props.MusicProperties.Title = track.Title;
            props.MusicProperties.Artist = track.DisplayArtist;
            props.MusicProperties.AlbumTitle = track.Album ?? string.Empty;
            if (track.ArtworkPath is { } web && web.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                props.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(web));
            else if (track.ArtworkPath is { } art && File.Exists(art))
                props.Thumbnail = RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(art));
            item.ApplyDisplayProperties(props);
            _player.Source = item;
            if (autoPlay) _player.Play();
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            _logger.LogWarning(ex, "Can't play {Path}", track.FilePath);
            OnFailed("The file couldn't be opened. It may have been moved or deleted.");
        }
        catch (Exception ex) when (IsPreview(track) && ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Preview failed for {Url}", track.FilePath);
            OnFailed("The stream couldn't be loaded. " + ex.Message);
        }
    }

    private void OnFailed(string? message)
    {
        _notifications.ShowWarning("Can't play this song", $"{Current?.Title}: {message}");
        // Skip to the next song instead of stopping the whole queue.
        if (CurrentIndex < Queue.Count - 1) _ = LoadAsync(CurrentIndex + 1, autoPlay: true);
    }

    private void UpdatePosition()
    {
        if (Current is null) return;
        var session = _player.PlaybackSession;
        _updatingPosition = true;
        try
        {
            if (session.NaturalDuration.TotalSeconds > 0) Duration = session.NaturalDuration.TotalSeconds;
            Position = Math.Min(session.Position.TotalSeconds, Duration);
        }
        finally
        {
            _updatingPosition = false;
        }
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (Current is null) return;
        if (IsPlaying) _player.Pause();
        else _player.Play();
    }

    [RelayCommand]
    private Task Next() => NextCoreAsync(userInitiated: true);

    private async Task NextCoreAsync(bool userInitiated)
    {
        if (Queue.Count == 0) return;
        if (!userInitiated && Repeat == RepeatMode.One)
        {
            _player.PlaybackSession.Position = TimeSpan.Zero;
            _player.Play();
            return;
        }

        int next;
        if (Shuffle && Queue.Count > 1)
        {
            _shuffleHistory.Push(CurrentIndex);
            do next = _random.Next(Queue.Count);
            while (next == CurrentIndex);
        }
        else
        {
            next = CurrentIndex + 1;
            if (next >= Queue.Count)
            {
                if (Repeat == RepeatMode.Off && !userInitiated)
                {
                    _player.Pause();
                    _player.PlaybackSession.Position = TimeSpan.Zero;
                    return;
                }

                next = 0;
            }
        }

        await LoadAsync(next, autoPlay: true);
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        if (Queue.Count == 0) return;
        // Like most players: restart the song unless it just started.
        if (_player.PlaybackSession.Position.TotalSeconds > 3)
        {
            _player.PlaybackSession.Position = TimeSpan.Zero;
            return;
        }

        var previous = Shuffle && _shuffleHistory.Count > 0 ? _shuffleHistory.Pop() : CurrentIndex - 1;
        if (previous < 0) previous = Repeat == RepeatMode.All ? Queue.Count - 1 : 0;
        await LoadAsync(previous, autoPlay: true);
    }

    [RelayCommand]
    private void ToggleShuffle() => Shuffle = !Shuffle;

    [RelayCommand]
    private void CycleRepeat() => Repeat = Repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    [RelayCommand]
    private Task PlayQueueItem(Track track) => LoadAsync(Queue.IndexOf(track), autoPlay: true);

    [RelayCommand]
    private void RevealCurrent()
    {
        if (Current is null) return;
        if (IsPreview(Current)) _launcher.OpenUri(Current.FilePath);
        else _launcher.RevealInExplorer(Current.FilePath);
    }

    [RelayCommand]
    private void Close()
    {
        _player.Pause();
        _player.Source = null;
        Queue.Clear();
        Current = null;
        CurrentIndex = -1;
        IsPlaying = false;
    }

    public void Dispose()
    {
        _timer.Stop();
        _player.Dispose();
    }
}
