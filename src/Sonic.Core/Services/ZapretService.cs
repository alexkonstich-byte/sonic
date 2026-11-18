using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;

namespace Sonic.Core.Services;

public sealed record ZapretStatus(bool Installed, bool Running, string? Folder, IReadOnlyList<string> Strategies, bool ServiceInstalled);

public sealed record ReachabilityResult(bool InternetReachable, bool YouTubeReachable, bool VideoServersReachable, string Details)
{
    public bool AllGood => YouTubeReachable && VideoServersReachable;

    /// <summary>The internet works but YouTube doesn't: the typical sign of provider blocking.</summary>
    public bool LooksBlocked => InternetReachable && !AllGood;
}

/// <summary>
/// Integration with the user's own zapret installation (DPI bypass used where YouTube is blocked or throttled by the
/// network provider). Sonic only finds, starts and stops it; zapret itself is not bundled.
/// </summary>
public interface IZapretService
{
    event EventHandler? StatusChanged;

    ZapretStatus GetStatus();

    /// <summary>Validates a folder chosen by the user. Returns an error, or null when it looks like zapret.</summary>
    string? ValidateFolder(string folder);

    /// <summary>Starts the strategy .bat elevated (Windows asks for administrator approval).</summary>
    Task StartAsync(string strategy, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task<ReachabilityResult> CheckYouTubeAsync(CancellationToken cancellationToken = default);

    /// <summary>True when a failure looks like a network block rather than a problem with the video.</summary>
    bool LooksLikeBlocking(string? error);

    /// <summary>
    /// Downloads the latest zapret-discord-youtube release from its official GitHub repository, verifies its
    /// SHA-256 and unpacks it into Sonic's tools folder. Returns the folder.
    /// </summary>
    Task<string> InstallAsync(IProgress<InstallProgress>? progress, CancellationToken cancellationToken = default);
}

public sealed partial class ZapretService(ISettingsService settings, IHttpClientFactory httpFactory, IAppPaths paths, ILogger<ZapretService> logger) : IZapretService
{
    private const string ProcessName = "winws";
    private const string Repository = "Flowseal/zapret-discord-youtube";

    public async Task<string> InstallAsync(IProgress<InstallProgress>? progress, CancellationToken cancellationToken = default)
    {
        var http = httpFactory.CreateClient(DependencyService.HttpClientName);
        progress?.Report(new InstallProgress("Looking up the latest zapret release", null));
        var (tag, url, sha256) = await FindLatestReleaseAsync(http, cancellationToken).ConfigureAwait(false);
        if (sha256 is null)
            throw new InvalidOperationException("The official checksum for zapret couldn't be read, so it wasn't installed.");

        var work = Path.Combine(paths.TempDirectory, "zapret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var zip = Path.Combine(work, "zapret.zip");
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = File.Create(zip);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    read += n;
                    progress?.Report(new InstallProgress($"Downloading zapret {tag}", total > 0 ? read * 100.0 / total : null));
                }
            }

            progress?.Report(new InstallProgress("Verifying checksum", null));
            await using (var stream = File.OpenRead(zip))
            {
                var actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The downloaded zapret archive failed checksum verification and was discarded.");
            }

            progress?.Report(new InstallProgress("Extracting", null));
            var destination = Path.Combine(paths.ToolsDirectory, "zapret");
            if (Directory.Exists(destination) && IsRunning())
                throw new InvalidOperationException("Stop zapret before updating it.");
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(zip, destination), cancellationToken).ConfigureAwait(false);

            // The archive may contain a top-level folder.
            var folder = Directory.EnumerateFiles(destination, "winws.exe", SearchOption.AllDirectories)
                .Select(f => Path.GetDirectoryName(Path.GetDirectoryName(f)!)!)
                .FirstOrDefault(d => ValidateFolder(d) is null);
            if (folder is null)
                throw new InvalidOperationException("zapret was downloaded, but bin\\winws.exe is missing — your antivirus may have quarantined it. Allow it and install again.");

            await settings.UpdateAsync(s => s.ZapretFolder = folder).ConfigureAwait(false);
            logger.LogInformation("Installed zapret {Tag} to {Folder}", tag, folder);
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return folder;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>Uses the GitHub API, or the public release page when the API is rate-limited.</summary>
    private async Task<(string Tag, string Url, string? Sha256)> FindLatestReleaseAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct).ConfigureAwait(false));
            var tag = doc.RootElement.GetProperty("tag_name").GetString()!;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                var assetDigest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                return (tag, asset.GetProperty("browser_download_url").GetString()!, assetDigest?.Replace("sha256:", string.Empty));
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation("GitHub API unavailable ({Message}); using the release page", ex.Message);
        }

        // github.com/…/releases/latest redirects to …/releases/tag/<tag>.
        using var latest = await http.GetAsync($"https://github.com/{Repository}/releases/latest", ct).ConfigureAwait(false);
        var finalUrl = latest.RequestMessage?.RequestUri?.AbsoluteUri ?? string.Empty;
        var tagFromUrl = finalUrl[(finalUrl.LastIndexOf('/') + 1)..];
        if (string.IsNullOrWhiteSpace(tagFromUrl) || tagFromUrl == "latest") throw new InvalidOperationException("The latest zapret release couldn't be found.");

        var html = await http.GetStringAsync($"https://github.com/{Repository}/releases/expanded_assets/{tagFromUrl}", ct).ConfigureAwait(false);
        var link = ZipLinkRegex().Match(html);
        if (!link.Success) throw new InvalidOperationException("The zapret release has no .zip download.");
        // The SHA-256 digest is listed next to each asset.
        var after = html[link.Index..Math.Min(html.Length, link.Index + 4000)];
        var digest = Sha256Regex().Match(after);
        return (tagFromUrl, "https://github.com" + link.Groups[1].Value, digest.Success ? digest.Groups[1].Value : null);
    }

    [System.Text.RegularExpressions.GeneratedRegex("href=\"(/Flowseal/zapret-discord-youtube/releases/download/[^\"]+\\.zip)\"")]
    private static partial System.Text.RegularExpressions.Regex ZipLinkRegex();

    [System.Text.RegularExpressions.GeneratedRegex("sha256:([0-9a-f]{64})")]
    private static partial System.Text.RegularExpressions.Regex Sha256Regex();

    public event EventHandler? StatusChanged;

    public ZapretStatus GetStatus()
    {
        var folder = FindFolder();
        var strategies = folder is null ? [] : ListStrategies(folder);
        var serviceInstalled = folder is not null && ServiceExists();
        return new ZapretStatus(folder is not null, IsRunning(), folder, strategies, serviceInstalled);
    }

    private string? FindFolder()
    {
        var configured = settings.Current.ZapretFolder;
        if (!string.IsNullOrWhiteSpace(configured) && ValidateFolder(configured) is null) return configured;

        // Common places people unpack zapret to.
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\",
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "*zapret*"))
                {
                    if (ValidateFolder(dir) is null) return dir;
                    // Archives often unpack into a nested folder.
                    foreach (var nested in Directory.EnumerateDirectories(dir))
                        if (ValidateFolder(nested) is null) return nested;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Skipping {Root}", root);
            }
        }

        return null;
    }

    public string? ValidateFolder(string folder)
    {
        if (!Directory.Exists(folder)) return "The folder doesn't exist.";
        if (!File.Exists(Path.Combine(folder, "bin", "winws.exe"))) return "This folder doesn't contain bin\\winws.exe — choose the unpacked zapret folder.";
        if (ListStrategies(folder).Count == 0) return "No strategy .bat files (general*.bat) were found in this folder.";
        return null;
    }

    private static List<string> ListStrategies(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.bat")
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => !n.StartsWith("service", StringComparison.OrdinalIgnoreCase)
                            && !n.Contains("discord", StringComparison.OrdinalIgnoreCase)
                            && !n.Contains("update", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.Equals("general.bat", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsRunning()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    private static bool ServiceExists()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret");
            return key is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public async Task StartAsync(string strategy, CancellationToken cancellationToken = default)
    {
        var status = GetStatus();
        if (status.Folder is null) throw new InvalidOperationException("zapret wasn't found. Choose its folder in Settings › Integrations › zapret.");
        if (status.Running) return;
        var bat = Path.Combine(status.Folder, strategy);
        if (!File.Exists(bat) || !status.Strategies.Contains(strategy, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The strategy \"{strategy}\" doesn't exist in {status.Folder}.");

        // The strategy script starts winws.exe minimized; it needs administrator rights for the WinDivert driver.
        RunElevated("cmd.exe", $"/c \"\"{bat}\"\"", status.Folder);
        logger.LogInformation("Started zapret strategy {Strategy}", strategy);
        await WaitForAsync(running: true, cancellationToken).ConfigureAwait(false);
        await settings.UpdateAsync(s => s.ZapretStrategy = strategy).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning()) return;
        // winws.exe runs elevated, so stopping it needs elevation too.
        RunElevated("taskkill.exe", "/F /IM winws.exe", Environment.SystemDirectory);
        logger.LogInformation("Stopped zapret");
        await WaitForAsync(running: false, cancellationToken).ConfigureAwait(false);
    }

    private void RunElevated(string file, string arguments, string workingDirectory)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = workingDirectory,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Administrator permission was declined.");
        }
    }

    private async Task WaitForAsync(bool running, CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
        {
            if (IsRunning() == running) break;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
        if (IsRunning() != running)
            throw new InvalidOperationException(running ? "zapret didn't start. Try another strategy or run it manually once." : "zapret is still running.");
    }

    public async Task<ReachabilityResult> CheckYouTubeAsync(CancellationToken cancellationToken = default)
    {
        var http = httpFactory.CreateClient(DependencyService.HttpClientName);
        async Task<(bool Ok, string Text)> Probe(string name, string url)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(7));
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                return (true, $"{name}: OK ({sw.ElapsedMilliseconds} ms)");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (false, $"{name}: timed out");
            }
            catch (HttpRequestException ex)
            {
                return (false, $"{name}: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        // Windows' own connectivity check: tells "no internet" apart from "YouTube is blocked".
        var internet = await Probe("Internet", "http://www.msftconnecttest.com/connecttest.txt").ConfigureAwait(false);
        var site = await Probe("youtube.com", "https://www.youtube.com/generate_204").ConfigureAwait(false);
        // Video data comes from googlevideo.com, which is what providers usually block or throttle.
        var video = await Probe("Video servers", "https://redirector.googlevideo.com/generate_204").ConfigureAwait(false);
        return new ReachabilityResult(internet.Ok, site.Ok, video.Ok, $"{site.Text} · {video.Text}" + (internet.Ok ? string.Empty : " · no internet connection"));
    }

    public bool LooksLikeBlocking(string? error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        var e = error.ToLowerInvariant();
        return e.Contains("network error") || e.Contains("timed out") || e.Contains("connection")
               || e.Contains("unable to download webpage") || e.Contains("ssl") || e.Contains("10054") || e.Contains("reset by peer")
               || e.Contains("read timed out") || e.Contains("too long to answer");
    }
}
