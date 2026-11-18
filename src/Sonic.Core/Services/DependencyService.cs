using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;

namespace Sonic.Core.Services;

/// <summary>
/// Detects yt-dlp, FFmpeg and a JavaScript runtime (used by yt-dlp for YouTube), checks official release
/// feeds for updates and installs Sonic-managed copies into the app data folder (no admin rights needed).
/// Resolution order: user override → Sonic-managed copy → PATH.
/// </summary>
public sealed partial class DependencyService(
    IAppPaths paths,
    ISettingsService settings,
    IProcessRunner runner,
    IHttpClientFactory httpFactory,
    ILogger<DependencyService> logger) : IDependencyService
{
    public const string HttpClientName = "Sonic";

    // Official distribution sources.
    private const string YtDlpExeUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string YtDlpSumsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    private const string YtDlpLatestApi = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    // Windows builds linked from https://ffmpeg.org/download.html (gyan.dev release builds).
    private const string FfmpegZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const string FfmpegSumUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256";
    private const string FfmpegVersionUrl = "https://www.gyan.dev/ffmpeg/builds/release-version";
    private const string DenoZipUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";
    private const string DenoSumUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip.sha256sum";

    /// <summary>yt-dlp release that introduced <c>--js-runtimes</c>.</summary>
    private const string JsRuntimeOptionVersion = "2025.11.12";

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<ToolKind, ToolStatus> _status = new()
    {
        [ToolKind.YtDlp] = new ToolStatus(ToolKind.YtDlp, ToolState.Unknown),
        [ToolKind.Ffmpeg] = new ToolStatus(ToolKind.Ffmpeg, ToolState.Unknown),
        [ToolKind.Deno] = new ToolStatus(ToolKind.Deno, ToolState.Unknown),
    };

    private string? _jsRuntimeName;

    public event EventHandler<ToolStatus>? StatusChanged;

    private string ManagedYtDlp => Path.Combine(paths.ToolsDirectory, "yt-dlp.exe");
    private string ManagedFfmpegDir => Path.Combine(paths.ToolsDirectory, "ffmpeg");
    private string ManagedDeno => Path.Combine(paths.ToolsDirectory, "deno.exe");

    public string? FfprobePath { get; private set; }

    public ToolStatus GetStatus(ToolKind kind)
    {
        lock (_lock) return _status[kind];
    }

    public string RequireYtDlp()
    {
        var s = GetStatus(ToolKind.YtDlp);
        if (s.IsUsable && s.Path is not null && File.Exists(s.Path)) return s.Path;
        throw new DependencyMissingException(ToolKind.YtDlp, "yt-dlp is not installed. Install it from Settings › Integrations.");
    }

    public string RequireFfmpeg()
    {
        var s = GetStatus(ToolKind.Ffmpeg);
        if (s.IsUsable && s.Path is not null && File.Exists(s.Path)) return s.Path;
        throw new DependencyMissingException(ToolKind.Ffmpeg, "FFmpeg is not installed. Install it from Settings › Integrations.");
    }

    public IReadOnlyList<string> GetYtDlpRuntimeArguments()
    {
        var yt = GetStatus(ToolKind.YtDlp);
        var js = GetStatus(ToolKind.Deno);
        if (!js.IsUsable || js.Path is null || _jsRuntimeName is null) return [];
        if (VersionHelper.Compare(yt.Version, JsRuntimeOptionVersion) < 0) return [];
        return ["--js-runtimes", $"{_jsRuntimeName}:{js.Path}"];
    }

    public async Task RefreshAsync(bool checkForUpdates, CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var kind in Enum.GetValues<ToolKind>())
            {
                var previous = GetStatus(kind);
                SetStatus(previous with { State = ToolState.Checking, Message = null });
            }

            await Task.WhenAll(
                DetectYtDlpAsync(checkForUpdates, cancellationToken),
                DetectFfmpegAsync(checkForUpdates, cancellationToken),
                DetectJsRuntimeAsync(cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task DetectYtDlpAsync(bool checkForUpdates, CancellationToken ct)
    {
        try
        {
            var path = Resolve(settings.Current.YtDlpPathOverride, ManagedYtDlp, "yt-dlp.exe");
            if (path is null)
            {
                SetStatus(new ToolStatus(ToolKind.YtDlp, ToolState.Missing, Message: "yt-dlp was not found."));
                return;
            }

            var result = await runner.RunAsync(new ProcessRequest
            {
                FileName = path,
                Arguments = ["--version"],
                CaptureStandardOutput = true,
                Timeout = TimeSpan.FromSeconds(30),
            }, cancellationToken: ct).ConfigureAwait(false);
            var version = result.StandardOutput.Trim().Split('\n').FirstOrDefault()?.Trim();
            if (result.ExitCode != 0 || string.IsNullOrEmpty(version))
            {
                SetStatus(new ToolStatus(ToolKind.YtDlp, ToolState.Error, path, Message: "yt-dlp did not run correctly. " + result.StandardErrorTail));
                return;
            }

            var managed = IsSamePath(path, ManagedYtDlp);
            string? latest = null;
            if (checkForUpdates) latest = await TryGetLatestYtDlpVersionAsync(ct).ConfigureAwait(false);
            var state = latest is not null && VersionHelper.Compare(version, latest) < 0 ? ToolState.UpdateAvailable : ToolState.Ready;
            SetStatus(new ToolStatus(ToolKind.YtDlp, state, path, version, latest, managed,
                state == ToolState.UpdateAvailable ? $"Version {latest} is available. Outdated versions often fail on YouTube." : null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "yt-dlp detection failed");
            SetStatus(new ToolStatus(ToolKind.YtDlp, ToolState.Error, Message: ex.Message));
        }
    }

    private async Task DetectFfmpegAsync(bool checkForUpdates, CancellationToken ct)
    {
        try
        {
            var overridePath = settings.Current.FfmpegPathOverride;
            if (!string.IsNullOrWhiteSpace(overridePath) && Directory.Exists(overridePath)) overridePath = Path.Combine(overridePath, "ffmpeg.exe");
            var path = Resolve(overridePath, Path.Combine(ManagedFfmpegDir, "ffmpeg.exe"), "ffmpeg.exe");
            if (path is null)
            {
                FfprobePath = null;
                SetStatus(new ToolStatus(ToolKind.Ffmpeg, ToolState.Missing, Message: "FFmpeg was not found."));
                return;
            }

            var probe = Path.Combine(Path.GetDirectoryName(path)!, "ffprobe.exe");
            FfprobePath = File.Exists(probe) ? probe : FindOnPath("ffprobe.exe");

            var result = await runner.RunAsync(new ProcessRequest
            {
                FileName = path,
                Arguments = ["-hide_banner", "-version"],
                CaptureStandardOutput = true,
                Timeout = TimeSpan.FromSeconds(20),
            }, cancellationToken: ct).ConfigureAwait(false);
            var firstLine = result.StandardOutput.Split('\n').FirstOrDefault() ?? string.Empty;
            var match = FfmpegVersionRegex().Match(firstLine);
            if (result.ExitCode != 0 || !match.Success)
            {
                SetStatus(new ToolStatus(ToolKind.Ffmpeg, ToolState.Error, path, Message: "FFmpeg did not run correctly."));
                return;
            }

            var rawVersion = match.Groups[1].Value;
            var version = VersionHelper.Extract(rawVersion) is { } v && rawVersion.StartsWith(v, StringComparison.Ordinal) ? v : rawVersion;
            var managed = IsSamePath(Path.GetDirectoryName(path), ManagedFfmpegDir);
            string? latest = null;
            if (checkForUpdates && VersionHelper.Extract(version) is not null)
                latest = await TryGetTextAsync(FfmpegVersionUrl, ct).ConfigureAwait(false);
            latest = VersionHelper.Extract(latest);
            var state = latest is not null && VersionHelper.Compare(version, latest) < 0 ? ToolState.UpdateAvailable : ToolState.Ready;
            var message = FfprobePath is null ? "ffprobe was not found next to FFmpeg; Sonic will re-encode instead of copying streams." : null;
            SetStatus(new ToolStatus(ToolKind.Ffmpeg, state, path, version, latest, managed, message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "FFmpeg detection failed");
            SetStatus(new ToolStatus(ToolKind.Ffmpeg, ToolState.Error, Message: ex.Message));
        }
    }

    private async Task DetectJsRuntimeAsync(CancellationToken ct)
    {
        try
        {
            var deno = File.Exists(ManagedDeno) ? ManagedDeno : FindOnPath("deno.exe");
            string? name = null, path = null;
            if (deno is not null) { name = "deno"; path = deno; }
            else if (FindOnPath("node.exe") is { } node) { name = "node"; path = node; }

            if (path is null)
            {
                _jsRuntimeName = null;
                SetStatus(new ToolStatus(ToolKind.Deno, ToolState.Missing,
                    Message: "Optional. yt-dlp needs a JavaScript runtime (Deno) for full YouTube support."));
                return;
            }

            var result = await runner.RunAsync(new ProcessRequest
            {
                FileName = path,
                Arguments = ["--version"],
                CaptureStandardOutput = true,
                Timeout = TimeSpan.FromSeconds(20),
            }, cancellationToken: ct).ConfigureAwait(false);
            var version = VersionHelper.Extract(result.StandardOutput);
            _jsRuntimeName = name;
            SetStatus(new ToolStatus(ToolKind.Deno, result.ExitCode == 0 ? ToolState.Ready : ToolState.Error, path, version,
                IsManaged: IsSamePath(path, ManagedDeno), Message: name == "node" ? "Using Node.js" : "Using Deno"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "JavaScript runtime detection failed");
            SetStatus(new ToolStatus(ToolKind.Deno, ToolState.Error, Message: ex.Message));
        }
    }

    public async Task InstallAsync(ToolKind kind, IProgress<InstallProgress>? progress, CancellationToken cancellationToken = default)
    {
        var work = Path.Combine(paths.TempDirectory, "install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var before = GetStatus(kind);
        SetStatus(before with { State = ToolState.Installing, Message = "Installing…" });
        try
        {
            switch (kind)
            {
                case ToolKind.YtDlp:
                {
                    var exe = Path.Combine(work, "yt-dlp.exe");
                    await DownloadFileAsync(YtDlpExeUrl, exe, "Downloading yt-dlp", progress, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new InstallProgress("Verifying checksum", null));
                    var sums = await GetTextOrThrowAsync(YtDlpSumsUrl, cancellationToken).ConfigureAwait(false);
                    var expected = sums.Split('\n')
                        .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        .FirstOrDefault(p => p.Length == 2 && p[1] == "yt-dlp.exe")?[0];
                    await VerifyAsync(exe, expected, cancellationToken).ConfigureAwait(false);
                    File.Move(exe, ManagedYtDlp, overwrite: true);
                    break;
                }

                case ToolKind.Ffmpeg:
                {
                    var zip = Path.Combine(work, "ffmpeg.zip");
                    await DownloadFileAsync(FfmpegZipUrl, zip, "Downloading FFmpeg", progress, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new InstallProgress("Verifying checksum", null));
                    var expected = Sha256Regex().Match(await GetTextOrThrowAsync(FfmpegSumUrl, cancellationToken).ConfigureAwait(false)).Value;
                    await VerifyAsync(zip, expected, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new InstallProgress("Extracting", null));
                    var extract = Path.Combine(work, "x");
                    await Task.Run(() => ZipFile.ExtractToDirectory(zip, extract), cancellationToken).ConfigureAwait(false);
                    var ffmpeg = Directory.EnumerateFiles(extract, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault()
                        ?? throw new InvalidOperationException("The FFmpeg archive did not contain ffmpeg.exe.");
                    var bin = Path.GetDirectoryName(ffmpeg)!;
                    Directory.CreateDirectory(ManagedFfmpegDir);
                    foreach (var file in Directory.EnumerateFiles(bin, "*.exe"))
                        File.Copy(file, Path.Combine(ManagedFfmpegDir, Path.GetFileName(file)), overwrite: true);
                    break;
                }

                case ToolKind.Deno:
                {
                    var zip = Path.Combine(work, "deno.zip");
                    await DownloadFileAsync(DenoZipUrl, zip, "Downloading Deno", progress, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new InstallProgress("Verifying checksum", null));
                    var expected = Sha256Regex().Match(await GetTextOrThrowAsync(DenoSumUrl, cancellationToken).ConfigureAwait(false)).Value;
                    await VerifyAsync(zip, expected, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new InstallProgress("Extracting", null));
                    var extract = Path.Combine(work, "x");
                    await Task.Run(() => ZipFile.ExtractToDirectory(zip, extract), cancellationToken).ConfigureAwait(false);
                    var deno = Directory.EnumerateFiles(extract, "deno.exe", SearchOption.AllDirectories).FirstOrDefault()
                        ?? throw new InvalidOperationException("The Deno archive did not contain deno.exe.");
                    File.Copy(deno, ManagedDeno, overwrite: true);
                    break;
                }
            }

            progress?.Report(new InstallProgress("Checking installation", null));
            logger.LogInformation("Installed managed {Tool}", kind);
        }
        catch
        {
            SetStatus(before); // keep showing the previous (working or missing) state
            throw;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (Exception ex) { logger.LogDebug(ex, "Could not clean install folder {Dir}", work); }
        }

        await RefreshAsync(checkForUpdates: settings.Current.CheckForToolUpdates, cancellationToken).ConfigureAwait(false);
        var status = GetStatus(kind);
        if (!status.IsUsable) throw new InvalidOperationException($"{status.DisplayName} was installed but could not be started: {status.Message}");
    }

    private async Task DownloadFileAsync(string url, string destination, string stage, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(destination);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        var lastReport = 0L;
        while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (read - lastReport > 256 * 1024 || read == total)
            {
                lastReport = read;
                progress?.Report(new InstallProgress(stage, total > 0 ? read * 100.0 / total : null));
            }
        }
    }

    private static async Task VerifyAsync(string file, string? expectedSha256, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64)
            throw new InvalidOperationException("The official checksum could not be read, so the download was not installed.");
        await using var stream = File.OpenRead(file);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        if (!hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The downloaded file failed checksum verification and was discarded.");
    }

    private async Task<string?> TryGetLatestYtDlpVersionAsync(CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient(HttpClientName);
            var release = await http.GetFromJsonAsync<GitHubRelease>(YtDlpLatestApi, ct).ConfigureAwait(false);
            return release?.TagName;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Could not check for yt-dlp updates");
            return null;
        }
    }

    private async Task<string?> TryGetTextAsync(string url, CancellationToken ct)
    {
        try
        {
            return await GetTextOrThrowAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not fetch {Url}", url);
            return null;
        }
    }

    private async Task<string> GetTextOrThrowAsync(string url, CancellationToken ct) =>
        await httpFactory.CreateClient(HttpClientName).GetStringAsync(url, ct).ConfigureAwait(false);

    private void SetStatus(ToolStatus status)
    {
        lock (_lock) _status[status.Kind] = status;
        StatusChanged?.Invoke(this, status);
    }

    private static string? Resolve(string? overridePath, string managedPath, string exeName)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return Path.GetFullPath(overridePath);
        if (File.Exists(managedPath)) return managedPath;
        return FindOnPath(exeName);
    }

    private static string? FindOnPath(string exeName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }

        return null;
    }

    private static bool IsSamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"ffmpeg version (\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex FfmpegVersionRegex();

    [GeneratedRegex("[0-9a-fA-F]{64}")]
    private static partial Regex Sha256Regex();

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    }
}
