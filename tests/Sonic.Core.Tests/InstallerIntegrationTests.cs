using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;
using Sonic.Core.Services;
using Xunit.Abstractions;

namespace Sonic.Core.Tests;

/// <summary>
/// Downloads the real tools from their official sources into a temporary Sonic data folder.
/// Enabled with SONIC_INTEGRATION=1 (downloads ~150 MB).
/// </summary>
public sealed class InstallerIntegrationTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("sonic-inst").FullName;
    private ServiceProvider _sp = null!;

    private static bool Enabled => Environment.GetEnvironmentVariable("SONIC_INTEGRATION") == "1";

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(new XunitLoggerProvider(output)).SetMinimumLevel(LogLevel.Information));
        services.AddSonicCore("Sonic-Tests/1.0");
        services.AddSingleton<IAppPaths>(new AppPaths(Path.Combine(_root, "data")));
        _sp = services.BuildServiceProvider();
        await _sp.GetRequiredService<ISettingsService>().LoadAsync();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        try { Directory.Delete(_root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [SkippableTheory]
    [InlineData(ToolKind.YtDlp)]
    [InlineData(ToolKind.Ffmpeg)]
    [InlineData(ToolKind.Deno)]
    public async Task Installs_tool_from_official_source(ToolKind kind)
    {
        Skip.IfNot(Enabled);
        var deps = _sp.GetRequiredService<IDependencyService>();
        var stages = new List<string>();
        await deps.InstallAsync(kind, new Helpers.SyncProgress<InstallProgress>(p => { lock (stages) if (!stages.Contains(p.Stage)) stages.Add(p.Stage); }));
        var status = deps.GetStatus(kind);
        output.WriteLine($"{kind}: {status.State} v{status.Version} managed={status.IsManaged} at {status.Path}; stages: {string.Join(" → ", stages)}");
        Assert.True(status.IsUsable);
        Assert.Contains("Verifying checksum", stages);
        // The managed copy lives in Sonic's tools folder and wins over PATH.
        Assert.StartsWith(Path.Combine(_root, "data", "tools"), status.Path, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Installs_zapret_from_github()
    {
        Skip.IfNot(Enabled);
        var zapret = _sp.GetRequiredService<IZapretService>();
        var folder = await zapret.InstallAsync(null);
        var status = zapret.GetStatus();
        output.WriteLine($"zapret at {folder}: {status.Strategies.Count} strategies");
        Assert.StartsWith(Path.Combine(_root, "data", "tools"), folder, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(folder, "bin", "winws.exe")));
        Assert.NotEmpty(status.Strategies);
        Assert.Equal(folder, status.Folder);
    }

    [SkippableFact]
    public async Task Reachability_distinguishes_blocking_from_offline()
    {
        Skip.IfNot(Enabled);
        var reach = await _sp.GetRequiredService<IZapretService>().CheckYouTubeAsync();
        output.WriteLine($"{reach.Details} → internet={reach.InternetReachable} blocked={reach.LooksBlocked}");
        Assert.True(reach.InternetReachable);
        Assert.False(reach.LooksBlocked); // YouTube works on this machine right now
    }
}
