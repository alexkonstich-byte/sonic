using Sonic.Core.Models;

namespace Sonic.Core.Abstractions;

public interface ISettingsService
{
    /// <summary>A snapshot of the current settings. Do not mutate; use <see cref="UpdateAsync"/>.</summary>
    AppSettings Current { get; }

    event EventHandler<AppSettings>? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default);

    string? GetSpotifyClientSecret();

    Task SetSpotifyCredentialsAsync(string? clientId, string? clientSecret);
}
