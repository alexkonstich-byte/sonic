using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;
using Sonic.Core.Services;

namespace Sonic.Core.Tests;

public sealed class CookieTests : IAsyncLifetime
{
    private const string Sample =
        "# Netscape HTTP Cookie File\n" +
        "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1999999999\tLOGIN_INFO\tabc\n" +
        ".youtube.com\tTRUE\t/\tTRUE\t1999999999\tPREF\tf6=40000000\n" +
        ".google.com\tTRUE\t/\tTRUE\t1999999999\tNID\txyz\n" +
        "www.example.org\tFALSE\t/\tFALSE\t0\tsession\t1\n" +
        "# a comment\n" +
        "broken line without tabs\n";

    private readonly string _root = Directory.CreateTempSubdirectory("sonic-ck").FullName;
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSonicCore("Sonic-Tests/1.0");
        services.AddSingleton<IAppPaths>(new AppPaths(_root));
        _sp = services.BuildServiceProvider();
        await _sp.GetRequiredService<ISettingsService>().LoadAsync();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void Summarizes_netscape_cookies()
    {
        var s = CookieService.Summarize(Sample);
        Assert.Equal(4, s.Count);
        Assert.Equal(2, s.YouTubeCount);
        Assert.True(s.SignedInToGoogle);
        Assert.Equal("youtube.com", s.TopDomains[0]);
    }

    [Fact]
    public void No_session_cookie_means_not_signed_in() =>
        Assert.False(CookieService.Summarize(".youtube.com\tTRUE\t/\tTRUE\t0\tPREF\tx\n").SignedInToGoogle);

    [Fact]
    public async Task Imports_file_stores_encrypted_and_leases_temporary_copy()
    {
        var file = Path.Combine(_root, "cookies.txt");
        await File.WriteAllTextAsync(file, Sample.Replace("# Netscape HTTP Cookie File\n", string.Empty)); // header added automatically
        var cookies = _sp.GetRequiredService<ICookieService>();

        var summary = await cookies.ImportFromFileAsync(file);
        Assert.Equal(4, summary.Count);
        var status = cookies.GetStatus();
        Assert.Equal(CookieMode.Imported, status.Mode);
        Assert.Equal("cookies.txt", status.Source);

        // Stored encrypted: the cookie value must not appear in plain text.
        var stored = await File.ReadAllBytesAsync(Path.Combine(_root, "cookies.dat"));
        Assert.DoesNotContain("LOGIN_INFO", System.Text.Encoding.UTF8.GetString(stored));

        string temp;
        using (var lease = cookies.Acquire())
        {
            Assert.Equal("--cookies", lease.Arguments[0]);
            temp = lease.Arguments[1];
            Assert.StartsWith("# Netscape HTTP Cookie File", await File.ReadAllTextAsync(temp));
        }

        Assert.False(File.Exists(temp)); // deleted when the yt-dlp run ends

        await cookies.ClearAsync();
        Assert.Equal(CookieMode.None, cookies.GetStatus().Mode);
        Assert.Empty(cookies.Acquire().Arguments);
    }

    [Fact]
    public async Task Rejects_files_that_are_not_cookies()
    {
        var file = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(file, "hello world\nthis is not a cookie file");
        await Assert.ThrowsAsync<CookieException>(() => _sp.GetRequiredService<ICookieService>().ImportFromFileAsync(file));
    }

    [Fact]
    public async Task Live_browser_mode_passes_cookies_from_browser()
    {
        var cookies = _sp.GetRequiredService<ICookieService>();
        await cookies.UseBrowserLiveAsync(new BrowserProfile("Firefox", "default", @"C:\p", @"firefox:C:\p", false, false, false));
        Assert.Equal(["--cookies-from-browser", @"firefox:C:\p"], cookies.Acquire().Arguments);
    }

    [Fact]
    public void Explains_locked_and_encrypted_databases()
    {
        var chrome = new BrowserProfile("Google Chrome", "Default", "x", "chrome:x", true, false, false);
        Assert.Contains("Close it", CookieService.DescribeImportError(chrome, "ERROR: Could not copy Chrome cookie database.", false));
        Assert.Contains("cookies.txt", CookieService.DescribeImportError(chrome, "ERROR: Failed to decrypt with DPAPI", false));
    }

    [Fact]
    public void Detects_browsers_without_throwing()
    {
        var found = _sp.GetRequiredService<ICookieService>().DetectBrowsers();
        Assert.All(found, b => Assert.Contains(':', b.Spec));
    }
}
