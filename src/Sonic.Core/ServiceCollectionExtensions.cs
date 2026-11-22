using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Sonic.Core.Abstractions;
using Sonic.Core.Data;
using Sonic.Core.Processes;
using Sonic.Core.Repositories;
using Sonic.Core.Services;

namespace Sonic.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSonicCore(this IServiceCollection services, string userAgent)
    {
        services.AddSingleton<IAppPaths, AppPaths>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();

        services.AddHttpClient(DependencyService.HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            client.Timeout = TimeSpan.FromMinutes(10);
        }).ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseProxy = true,
            Proxy = new Helpers.SettingsWebProxy(sp.GetRequiredService<ISettingsService>()),
        });

        services.AddDbContextFactory<SonicDbContext>((sp, options) =>
        {
            var paths = sp.GetRequiredService<IAppPaths>();
            options.UseSqlite($"Data Source={paths.DatabasePath};Cache=Shared");
            // MetadataHints is intentionally optional: all-null hints load as null, which is what the pipeline expects.
            options.ConfigureWarnings(w => w.Ignore(RelationalEventId.OptionalDependentWithoutIdentifyingPropertyWarning));
        });
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        services.AddSingleton<IDownloadRepository, DownloadRepository>();
        services.AddSingleton<ITrackRepository, TrackRepository>();
        services.AddSingleton<IPlaylistRepository, PlaylistRepository>();

        services.AddSingleton<IDependencyService, DependencyService>();
        services.AddSingleton<ICookieService, CookieService>();
        services.AddSingleton<IZapretService, ZapretService>();
        services.AddSingleton<IYtDlpService, YtDlpService>();
        services.AddSingleton<IFfmpegService, FfmpegService>();
        services.AddSingleton<IMetadataService, MetadataService>();
        services.AddSingleton<ICoverArtService, CoverArtService>();
        services.AddSingleton<ISpotifyService, SpotifyService>();
        services.AddSingleton<ILibraryService, LibraryService>();
        services.AddSingleton<IPlaylistService, PlaylistService>();
        services.AddSingleton<IPlaylistDownloadService, PlaylistDownloadService>();
        services.AddSingleton<ISpotifyDownloadService, SpotifyDownloadService>();
        services.AddSingleton<IDownloadPipeline, DownloadPipeline>();
        services.AddSingleton<IDownloadQueueService, DownloadQueueService>();
        return services;
    }
}
