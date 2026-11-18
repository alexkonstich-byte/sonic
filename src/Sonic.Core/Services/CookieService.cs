using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;

namespace Sonic.Core.Services;

public sealed class CookieService : ICookieService
{
    private static readonly byte[] Entropy = "Sonic.Cookies.v1"u8.ToArray();
    private static readonly string[] SessionCookieNames = ["SAPISID", "__Secure-3PSID", "__Secure-1PSID", "SID", "LOGIN_INFO"];

    private readonly IDependencyService _dependencies;
    private readonly IProcessRunner _runner;
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly ILogger<CookieService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CookieSummary? _cachedSummary;
    private bool _summaryLoaded;

    public CookieService(IDependencyService dependencies, IProcessRunner runner, ISettingsService settings, IAppPaths paths, ILogger<CookieService> logger)
    {
        _dependencies = dependencies;
        _runner = runner;
        _settings = settings;
        _paths = paths;
        _logger = logger;
        SweepTemporaryFiles();
    }

    public event EventHandler? Changed;

    private string StorePath => Path.Combine(_paths.DataDirectory, "cookies.dat");

    // ---------------- Detection ----------------

    private sealed record ChromiumBrowser(string Name, string YtDlpName, string UserDataDir, string Process, bool Experimental, bool SingleProfile = false);

    private static IEnumerable<ChromiumBrowser> ChromiumBrowsers()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return new("Google Chrome", "chrome", Path.Combine(local, "Google", "Chrome", "User Data"), "chrome", false);
        yield return new("Microsoft Edge", "edge", Path.Combine(local, "Microsoft", "Edge", "User Data"), "msedge", false);
        yield return new("Brave", "brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"), "brave", false);
        yield return new("Vivaldi", "vivaldi", Path.Combine(local, "Vivaldi", "User Data"), "vivaldi", false);
        yield return new("Chromium", "chromium", Path.Combine(local, "Chromium", "User Data"), "chrome", false);
        yield return new("Opera", "opera", Path.Combine(roaming, "Opera Software", "Opera Stable"), "opera", false, SingleProfile: true);
        yield return new("Opera GX", "opera", Path.Combine(roaming, "Opera Software", "Opera GX Stable"), "opera", false, SingleProfile: true);
        // Not officially supported by yt-dlp; read through its Chrome support.
        yield return new("Yandex Browser", "chrome", Path.Combine(local, "Yandex", "YandexBrowser", "User Data"), "browser", true);
    }

    public IReadOnlyList<BrowserProfile> DetectBrowsers()
    {
        var result = new List<BrowserProfile>();
        try
        {
            result.AddRange(DetectFirefox());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Firefox profile detection failed");
        }

        foreach (var b in ChromiumBrowsers())
        {
            try
            {
                if (!Directory.Exists(b.UserDataDir)) continue;
                var running = IsRunning(b.Process);
                if (b.SingleProfile)
                {
                    if (HasChromiumCookies(b.UserDataDir))
                        result.Add(new BrowserProfile(b.Name, string.Empty, b.UserDataDir, $"{b.YtDlpName}:{b.UserDataDir}", true, running, b.Experimental));
                    continue;
                }

                var names = ReadChromiumProfileNames(b.UserDataDir);
                foreach (var dir in Directory.EnumerateDirectories(b.UserDataDir).Where(HasChromiumCookies).OrderBy(d => Path.GetFileName(d) == "Default" ? 0 : 1).ThenBy(d => d))
                {
                    var folder = Path.GetFileName(dir);
                    var display = names.TryGetValue(folder, out var n) && !string.IsNullOrWhiteSpace(n) ? n : folder;
                    result.Add(new BrowserProfile(b.Name, display, dir, $"{b.YtDlpName}:{dir}", true, running, b.Experimental));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "{Browser} profile detection failed", b.Name);
            }
        }

        return result;
    }

    private static IEnumerable<BrowserProfile> DetectFirefox()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox");
        var ini = Path.Combine(root, "profiles.ini");
        if (!File.Exists(ini)) yield break;

        // profiles.ini: [ProfileN] Name/Path/IsRelative; [Install…] Default= marks the profile Firefox actually uses.
        var sections = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        foreach (var raw in File.ReadAllLines(ini))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["__section"] = line.Trim('[', ']') };
                sections.Add(current);
            }
            else if (current is not null && line.IndexOf('=') is > 0 and var eq)
            {
                current[line[..eq]] = line[(eq + 1)..];
            }
        }

        var installDefaults = sections.Where(s => s["__section"].StartsWith("Install", StringComparison.OrdinalIgnoreCase) && s.ContainsKey("Default"))
            .Select(s => s["Default"].Replace('/', '\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var running = IsRunning("firefox");
        var profiles = new List<BrowserProfile>();
        foreach (var s in sections.Where(s => s["__section"].StartsWith("Profile", StringComparison.OrdinalIgnoreCase) && s.ContainsKey("Path")))
        {
            var rel = s["Path"].Replace('/', '\\');
            var path = s.GetValueOrDefault("IsRelative", "1") == "1" ? Path.Combine(root, rel) : rel;
            if (!File.Exists(Path.Combine(path, "cookies.sqlite"))) continue;
            var name = s.GetValueOrDefault("Name", Path.GetFileName(path));
            if (installDefaults.Contains(rel)) name += " (in use)";
            profiles.Add(new BrowserProfile("Firefox", name, path, $"firefox:{path}", false, running, false));
        }

        foreach (var p in profiles.OrderBy(p => p.ProfileName.EndsWith("(in use)", StringComparison.Ordinal) ? 0 : 1)) yield return p;
    }

    private static bool HasChromiumCookies(string profileDir) =>
        File.Exists(Path.Combine(profileDir, "Network", "Cookies")) || File.Exists(Path.Combine(profileDir, "Cookies"));

    private static Dictionary<string, string> ReadChromiumProfileNames(string userDataDir)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var localState = Path.Combine(userDataDir, "Local State");
        if (!File.Exists(localState)) return names;
        try
        {
            using var stream = new FileStream(localState, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("profile", out var profile) && profile.TryGetProperty("info_cache", out var cache))
            {
                foreach (var p in cache.EnumerateObject())
                {
                    if (p.Value.TryGetProperty("name", out var n) && n.GetString() is { } name) names[p.Name] = name;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Names are cosmetic; fall back to folder names.
        }

        return names;
    }

    private static bool IsRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    // ---------------- Import ----------------

    public async Task<CookieSummary> ImportFromBrowserAsync(BrowserProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.IsChromium && profile.IsRunning)
            throw new CookieException($"Close {profile.Browser} completely and try again: it locks its cookie database while it's running.");

        var exe = _dependencies.RequireYtDlp();
        var output = Path.Combine(_paths.TempDirectory, $"cookies-{Guid.NewGuid():N}.txt");
        try
        {
            // yt-dlp extracts and decrypts the browser cookies and writes them to --cookies before it
            // notices there's no URL, so nothing is downloaded and no network request is made.
            var result = await _runner.RunAsync(new ProcessRequest
            {
                FileName = exe,
                Arguments = ["--ignore-config", "--cookies-from-browser", profile.Spec, "--cookies", output],
                Timeout = TimeSpan.FromMinutes(2),
            }, cancellationToken: cancellationToken).ConfigureAwait(false);

            var text = File.Exists(output) ? await File.ReadAllTextAsync(output, cancellationToken).ConfigureAwait(false) : null;
            var summary = text is null ? null : Summarize(text);
            if (summary is not { Count: > 0 })
            {
                var stderr = result.StandardErrorTail;
                _logger.LogWarning("Cookie import from {Browser} failed: {Error}", profile.Browser, stderr);
                throw new CookieException(DescribeImportError(profile, stderr, result.TimedOut));
            }

            await StoreAsync(text!, profile.DisplayName).ConfigureAwait(false);
            _logger.LogInformation("Imported {Count} cookies from {Browser} ({YouTube} for YouTube)", summary.Count, profile.DisplayName, summary.YouTubeCount);
            return summary;
        }
        finally
        {
            TryDelete(output);
        }
    }

    internal static string DescribeImportError(BrowserProfile profile, string stderr, bool timedOut)
    {
        var e = stderr.ToLowerInvariant();
        if (timedOut) return "Reading the browser's cookies took too long.";
        if (e.Contains("could not copy")) return $"{profile.Browser} is locking its cookie database. Close it completely (check the system tray) and try again.";
        if (e.Contains("decrypt") || e.Contains("app-bound") || e.Contains("dpapi"))
            return $"{profile.Browser} encrypts its cookies in a way yt-dlp can't read. Use Firefox, or export a cookies.txt file with a browser extension and import that.";
        if (e.Contains("permission") || e.Contains("access is denied")) return $"Windows denied access to {profile.Browser}'s cookie database.";
        if (e.Contains("could not find") || e.Contains("not found")) return $"No cookie database was found for {profile.DisplayName}.";
        var error = stderr.Split('\n').LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal))?[6..].Trim();
        return error ?? $"No cookies were found in {profile.DisplayName}. Sign in to the site in that browser first.";
    }

    public async Task<CookieSummary> ImportFromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new CookieException("The file doesn't exist.");
        if (info.Length > 20 * 1024 * 1024) throw new CookieException("That file is too large to be a cookies.txt file.");
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var summary = Summarize(text);
        if (summary.Count == 0)
            throw new CookieException("This isn't a cookies.txt file in Netscape format (one cookie per line, 7 tab-separated fields).");
        if (!text.TrimStart().StartsWith("# Netscape HTTP Cookie File", StringComparison.OrdinalIgnoreCase)
            && !text.TrimStart().StartsWith("# HTTP Cookie File", StringComparison.OrdinalIgnoreCase))
            text = "# Netscape HTTP Cookie File\n" + text; // yt-dlp requires the header line.

        await StoreAsync(text, Path.GetFileName(path)).ConfigureAwait(false);
        _logger.LogInformation("Imported {Count} cookies from file {File}", summary.Count, Path.GetFileName(path));
        return summary;
    }

    /// <summary>A failing subscriber must not turn a successful import into an error.</summary>
    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cookie change handler failed");
        }
    }

    private async Task StoreAsync(string text, string source)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")), Entropy, DataProtectionScope.CurrentUser);
            var temp = StorePath + ".tmp";
            await File.WriteAllBytesAsync(temp, encrypted).ConfigureAwait(false);
            File.Move(temp, StorePath, overwrite: true);
            _cachedSummary = Summarize(text);
            _summaryLoaded = true;
        }
        finally
        {
            _gate.Release();
        }

        await _settings.UpdateAsync(s =>
        {
            s.CookieMode = CookieMode.Imported;
            s.CookiesImportedFrom = source;
            s.CookiesImportedAtUtc = DateTime.UtcNow;
        }).ConfigureAwait(false);
        RaiseChanged();
    }

    public async Task UseBrowserLiveAsync(BrowserProfile profile)
    {
        await _settings.UpdateAsync(s =>
        {
            s.CookieMode = CookieMode.Browser;
            s.CookieBrowserSpec = profile.Spec;
            s.CookieBrowserName = profile.DisplayName;
        }).ConfigureAwait(false);
        RaiseChanged();
    }

    public async Task SetModeAsync(CookieMode mode)
    {
        if (mode == CookieMode.Imported && !File.Exists(StorePath)) throw new CookieException("Import cookies first.");
        if (mode == CookieMode.Browser && string.IsNullOrEmpty(_settings.Current.CookieBrowserSpec)) throw new CookieException("Choose a browser first.");
        await _settings.UpdateAsync(s => s.CookieMode = mode).ConfigureAwait(false);
        RaiseChanged();
    }

    public async Task ClearAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            TryDelete(StorePath);
            _cachedSummary = null;
            _summaryLoaded = true;
        }
        finally
        {
            _gate.Release();
        }

        await _settings.UpdateAsync(s =>
        {
            s.CookieMode = CookieMode.None;
            s.CookiesImportedFrom = null;
            s.CookiesImportedAtUtc = null;
        }).ConfigureAwait(false);
        RaiseChanged();
    }

    // ---------------- Use ----------------

    public CookieStatus GetStatus()
    {
        var s = _settings.Current;
        if (!_summaryLoaded)
        {
            _cachedSummary = ReadStore() is { } text ? Summarize(text) : null;
            _summaryLoaded = true;
        }

        return new CookieStatus(s.CookieMode, _cachedSummary, s.CookiesImportedFrom, s.CookiesImportedAtUtc, s.CookieBrowserName);
    }

    public CookieLease Acquire()
    {
        var s = _settings.Current;
        switch (s.CookieMode)
        {
            case CookieMode.Browser when !string.IsNullOrEmpty(s.CookieBrowserSpec):
                return new CookieLease(["--cookies-from-browser", s.CookieBrowserSpec], null);

            case CookieMode.Imported:
                var text = ReadStore();
                if (text is null) return CookieLease.Empty;
                // Each run gets its own copy: yt-dlp rewrites the cookie file on exit, and parallel downloads must not collide.
                var temp = Path.Combine(_paths.TempDirectory, $"cookies-{Guid.NewGuid():N}.txt");
                File.WriteAllText(temp, text);
                return new CookieLease(["--cookies", temp], temp);

            default:
                return CookieLease.Empty;
        }
    }

    private string? ReadStore()
    {
        if (!File.Exists(StorePath)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(StorePath), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            _logger.LogError(ex, "Stored cookies could not be decrypted");
            return null;
        }
    }

    /// <summary>Counts cookies in a Netscape cookies.txt and checks whether a Google/YouTube session is present.</summary>
    internal static CookieSummary Summarize(string text)
    {
        var count = 0;
        var youtube = 0;
        var signedIn = false;
        var domains = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("#HttpOnly_", StringComparison.Ordinal)) line = line["#HttpOnly_".Length..];
            else if (line.Length == 0 || line.StartsWith('#')) continue;
            var f = line.Split('\t');
            if (f.Length < 7 || f[0].Length == 0) continue;

            count++;
            var domain = f[0].TrimStart('.');
            var isYouTube = domain.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || domain.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);
            var isGoogle = domain.Equals("google.com", StringComparison.OrdinalIgnoreCase) || domain.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase);
            if (isYouTube) youtube++;
            if ((isYouTube || isGoogle) && SessionCookieNames.Contains(f[5], StringComparer.Ordinal)) signedIn = true;

            var labels = domain.Split('.');
            var site = labels.Length >= 2 ? string.Join('.', labels[^2..]) : domain;
            domains[site] = domains.GetValueOrDefault(site) + 1;
        }

        var top = domains.OrderByDescending(d => d.Value).Take(5).Select(d => d.Key).ToList();
        return new CookieSummary(count, youtube, signedIn, top);
    }

    private void SweepTemporaryFiles()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_paths.TempDirectory, "cookies-*.txt")) TryDelete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Cookie temp sweep failed");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
