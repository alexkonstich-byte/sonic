using Sonic.Core.Models;

namespace Sonic.Core.Abstractions;

/// <summary>
/// Lets yt-dlp act as the user's own signed-in browser session (age checks, members-only content, bot checks).
/// Imported cookies are stored encrypted (DPAPI, current Windows user) and handed to yt-dlp only as a
/// short-lived temporary copy.
/// </summary>
public interface ICookieService
{
    event EventHandler? Changed;

    /// <summary>Finds installed browsers and their profiles.</summary>
    IReadOnlyList<BrowserProfile> DetectBrowsers();

    CookieStatus GetStatus();

    Task<CookieSummary> ImportFromBrowserAsync(BrowserProfile profile, CancellationToken cancellationToken = default);

    Task<CookieSummary> ImportFromFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Reads cookies live from the browser on every yt-dlp run instead of importing a snapshot.</summary>
    Task UseBrowserLiveAsync(BrowserProfile profile);

    Task SetModeAsync(CookieMode mode);

    /// <summary>Deletes the imported snapshot and stops using cookies.</summary>
    Task ClearAsync();

    /// <summary>Arguments for one yt-dlp run. Dispose the lease afterwards to delete the temporary copy.</summary>
    CookieLease Acquire();
}

public sealed class CookieLease(IReadOnlyList<string> arguments, string? temporaryFile) : IDisposable
{
    public static CookieLease Empty { get; } = new([], null);

    public IReadOnlyList<string> Arguments { get; } = arguments;

    public void Dispose()
    {
        if (temporaryFile is null) return;
        try
        {
            File.Delete(temporaryFile);
        }
        catch (IOException)
        {
            // Swept on next start.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
