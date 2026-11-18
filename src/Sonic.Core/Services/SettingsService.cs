using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;

namespace Sonic.Core.Services;

/// <summary>Persists <see cref="AppSettings"/> as JSON with atomic writes.</summary>
public sealed class SettingsService(IAppPaths paths, ILogger<SettingsService> logger) : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current = new();

    public AppSettings Current => _current;

    public event EventHandler<AppSettings>? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(paths.SettingsPath))
            {
                try
                {
                    await using (var stream = File.OpenRead(paths.SettingsPath))
                    {
                        // Files written before versioning existed have no SettingsVersion: treat them as version 1.
                        var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? new AppSettings();
                        _current = loaded;
                    }

                    if (Migrate(_current, await File.ReadAllTextAsync(paths.SettingsPath, cancellationToken).ConfigureAwait(false)))
                        await SaveCoreAsync(_current, cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    logger.LogError(ex, "Settings file is corrupt; backing it up and using defaults.");
                    File.Copy(paths.SettingsPath, paths.SettingsPath + ".corrupt", overwrite: true);
                    _current = new AppSettings();
                }
            }

            Normalize(_current);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default)
    {
        AppSettings snapshot;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var copy = _current.Clone();
            update(copy);
            Normalize(copy);
            await SaveCoreAsync(copy, cancellationToken).ConfigureAwait(false);
            _current = copy;
            snapshot = copy;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, snapshot);
    }

    public string? GetSpotifyClientSecret() => SecretProtector.Unprotect(_current.SpotifyClientSecretProtected);

    public Task SetSpotifyCredentialsAsync(string? clientId, string? clientSecret) =>
        UpdateAsync(s =>
        {
            s.SpotifyClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim();
            s.SpotifyClientSecretProtected = string.IsNullOrWhiteSpace(clientSecret) ? null : SecretProtector.Protect(clientSecret.Trim());
        });

    private async Task SaveCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var temp = paths.SettingsPath + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, paths.SettingsPath, overwrite: true);
    }

    /// <summary>
    /// Version 2 moved the default download folders from Music/Videos to Downloads. Settings still on the old
    /// defaults follow the new ones; the old music folder stays in the library so nothing disappears.
    /// </summary>
    internal static bool Migrate(AppSettings s, string json)
    {
        var version = json.Contains("\"SettingsVersion\"", StringComparison.Ordinal) ? s.SettingsVersion : 1;
        if (version >= AppSettings.CurrentVersion) return false;

        var oldMusic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Sonic");
        var oldVideo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Sonic");
        if (string.Equals(Path.TrimEndingDirectorySeparator(s.DownloadDirectory), oldMusic, StringComparison.OrdinalIgnoreCase))
        {
            s.DownloadDirectory = AppSettings.DefaultAudioDirectory;
            if (Directory.Exists(oldMusic) && !s.MusicFolders.Contains(oldMusic, StringComparer.OrdinalIgnoreCase))
                s.MusicFolders.Add(oldMusic);
        }

        if (string.Equals(Path.TrimEndingDirectorySeparator(s.VideoDirectory), oldVideo, StringComparison.OrdinalIgnoreCase))
            s.VideoDirectory = AppSettings.DefaultVideoDirectory;

        s.SettingsVersion = AppSettings.CurrentVersion;
        return true;
    }

    private static void Normalize(AppSettings s)
    {
        s.RecentFolders = s.RecentFolders
            .Where(f => FileNameHelper.ValidateDirectory(f) is null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        s.MaxConcurrentDownloads = Math.Clamp(s.MaxConcurrentDownloads, 1, 8);
        s.MaxRetries = Math.Clamp(s.MaxRetries, 0, 5);
        if (FileNameHelper.ValidateTemplate(s.FilenameTemplate) is not null) s.FilenameTemplate = FileNameHelper.DefaultTemplate;
        if (FileNameHelper.ValidateDirectory(s.DownloadDirectory) is not null) s.DownloadDirectory = new AppSettings().DownloadDirectory;
        if (FileNameHelper.ValidateTemplate(s.VideoFilenameTemplate, MediaKind.Video) is not null) s.VideoFilenameTemplate = FileNameHelper.DefaultVideoTemplate;
        if (FileNameHelper.ValidateDirectory(s.VideoDirectory) is not null) s.VideoDirectory = new AppSettings().VideoDirectory;
        s.DefaultVideoMaxHeight = Math.Clamp(s.DefaultVideoMaxHeight, 0, 4320);
        s.DefaultSubtitleLanguages = s.DefaultSubtitleLanguages?.Trim() ?? string.Empty;
        s.SpeedLimitKBps = Math.Clamp(s.SpeedLimitKBps, 0, 1_000_000);
        if (NetworkOptions.ValidateProxy(s.ProxyUrl) is not null) s.ProxyUrl = null;
        s.ProxyUrl = string.IsNullOrWhiteSpace(s.ProxyUrl) ? null : s.ProxyUrl.Trim();
        s.MusicFolders = s.MusicFolders
            .Where(f => FileNameHelper.ValidateDirectory(f) is null)
            .Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
