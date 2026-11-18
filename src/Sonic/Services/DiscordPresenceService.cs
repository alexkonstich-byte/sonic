using System.ComponentModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Services;
using Sonic.ViewModels;

namespace Sonic.Services;

/// <summary>
/// Shows the song playing in Sonic's player as the user's Discord status ("Listening to …") through the local
/// Discord app. Off by default; needs a Discord Application ID (its name is what Discord displays).
/// </summary>
public sealed partial class DiscordPresenceService : ObservableObject, IAsyncDisposable
{
    private sealed record Snapshot(bool HasTrack, bool IsPlaying, string Title, string Artist, string? Album, string? Image, double Position, double Duration);

    private readonly PlayerViewModel _player;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<DiscordPresenceService> _logger;
    private readonly Channel<Snapshot> _updates = Channel.CreateBounded<Snapshot>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    private DispatcherQueueTimer? _timer;
    private DiscordRpcClient? _client;
    private string? _clientId;
    private Snapshot? _lastSent;
    private DateTime _lastSentAt;

    public DiscordPresenceService(PlayerViewModel player, ISettingsService settings, IUiDispatcher dispatcher, ILogger<DiscordPresenceService> logger)
    {
        _player = player;
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    [ObservableProperty] private string _status = "Off";

    public void Start()
    {
        _player.PropertyChanged += OnPlayerChanged;
        _settings.Changed += (_, _) => _dispatcher.Post(Queue);
        // Re-sync now and then: catches seeks, and connects once Discord is started.
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(15);
        _timer.Tick += (_, _) => Queue();
        _timer.Start();
        _ = Task.Run(() => RunAsync(_cts.Token));
        Queue();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.Current) or nameof(PlayerViewModel.IsPlaying) or nameof(PlayerViewModel.Duration)) Queue();
    }

    /// <summary>Takes a snapshot of the player on the UI thread and hands it to the background sender.</summary>
    private void Queue()
    {
        var track = _player.Current;
        string? image = null;
        if (track is not null)
        {
            if (track.ArtworkPath is { } art && art.StartsWith("http", StringComparison.OrdinalIgnoreCase)) image = art;
            else if (SourceDetector.CanonicalKey(track.SourceUrl) is { } key && key.StartsWith("youtube:", StringComparison.Ordinal))
                image = $"https://i.ytimg.com/vi/{key["youtube:".Length..]}/hqdefault.jpg";
        }

        _updates.Writer.TryWrite(new Snapshot(
            track is not null, _player.IsPlaying, track?.Title ?? string.Empty, track?.DisplayArtist ?? string.Empty,
            string.IsNullOrWhiteSpace(track?.Album) ? null : track.Album, image, _player.Position, _player.Duration));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await foreach (var snapshot in _updates.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await ApplyAsync(snapshot, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Discord status update failed");
                SetStatus(ex is InvalidOperationException ? "Discord: " + ex.Message : "Not connected (is Discord running?)");
                await DropClientAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ApplyAsync(Snapshot s, CancellationToken ct)
    {
        var settings = _settings.Current;
        var id = settings.DiscordClientId?.Trim();
        if (!settings.DiscordPresence || string.IsNullOrEmpty(id))
        {
            if (_client is not null)
            {
                try { await _client.SetActivityAsync(null, ct).ConfigureAwait(false); }
                catch (IOException) { }
                await DropClientAsync().ConfigureAwait(false);
            }

            SetStatus(settings.DiscordPresence ? "Enter your Discord Application ID." : "Off");
            return;
        }

        if (_client is null || _clientId != id)
        {
            await DropClientAsync().ConfigureAwait(false);
            _client = new DiscordRpcClient(id);
            _clientId = id;
        }

        if (!_client.IsConnected)
        {
            if (!await _client.ConnectAsync(ct).ConfigureAwait(false))
            {
                SetStatus("Waiting for Discord (it isn't running).");
                return;
            }

            _lastSent = null;
        }

        // Skip identical updates; a timer tick only re-sends when the position drifted (a seek).
        if (_lastSent is { } last && last with { Position = 0 } == s with { Position = 0 })
        {
            var expected = last.Position + (s.IsPlaying ? (DateTime.UtcNow - _lastSentAt).TotalSeconds : 0);
            if (Math.Abs(expected - s.Position) < 3) return;
        }

        DiscordActivity? activity = null;
        if (s.HasTrack)
        {
            var now = DateTimeOffset.UtcNow;
            var start = s.IsPlaying ? now.AddSeconds(-s.Position) : (DateTimeOffset?)null;
            var end = s.IsPlaying && s.Duration > 1 ? start!.Value.AddSeconds(s.Duration) : (DateTimeOffset?)null;
            activity = new DiscordActivity(s.Title, s.IsPlaying ? s.Artist : $"{s.Artist} · paused", s.Image, s.Album, start, end);
        }

        await _client.SetActivityAsync(activity, ct).ConfigureAwait(false);
        _lastSent = s;
        _lastSentAt = DateTime.UtcNow;
        SetStatus(activity is null ? "Connected. Nothing is playing." : $"Connected. Showing \"{s.Title}\".");
    }

    private async Task DropClientAsync()
    {
        if (_client is null) return;
        await _client.DisposeAsync().ConfigureAwait(false);
        _client = null;
        _clientId = null;
        _lastSent = null;
    }

    private void SetStatus(string text) => _dispatcher.Post(() => Status = text);

    public async ValueTask DisposeAsync()
    {
        _timer?.Stop();
        _player.PropertyChanged -= OnPlayerChanged;
        if (_client is { IsConnected: true } client)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await client.SetActivityAsync(null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
            {
            }
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        await DropClientAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
