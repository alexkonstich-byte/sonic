using Microsoft.Extensions.DependencyInjection;
using Sonic.Core.Abstractions;
using Sonic.Core.Services;
using Xunit.Abstractions;

namespace Sonic.Core.Tests;

public sealed class ZapretTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("sonic-zp").FullName;
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSonicCore("Sonic-Tests/1.0");
        services.AddSingleton<IAppPaths>(new AppPaths(Path.Combine(_root, "data")));
        _sp = services.BuildServiceProvider();
        await _sp.GetRequiredService<ISettingsService>().LoadAsync();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string MakeFakeZapret()
    {
        var dir = Path.Combine(_root, "zapret-test");
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        File.WriteAllText(Path.Combine(dir, "bin", "winws.exe"), "x");
        foreach (var n in new[] { "general.bat", "general (ALT).bat", "service.bat", "discord.bat" }) File.WriteAllText(Path.Combine(dir, n), "@echo off");
        return dir;
    }

    [Fact]
    public async Task Uses_configured_folder_and_lists_strategies()
    {
        var dir = MakeFakeZapret();
        var zapret = _sp.GetRequiredService<IZapretService>();
        Assert.Null(zapret.ValidateFolder(dir));
        await _sp.GetRequiredService<ISettingsService>().UpdateAsync(s => s.ZapretFolder = dir);
        var status = zapret.GetStatus();
        Assert.True(status.Installed);
        Assert.Equal(dir, status.Folder);
        Assert.Equal(["general.bat", "general (ALT).bat"], status.Strategies); // service/discord scripts are excluded
    }

    [Fact]
    public void Rejects_folders_without_winws()
    {
        var zapret = _sp.GetRequiredService<IZapretService>();
        Assert.NotNull(zapret.ValidateFolder(_root));
        Assert.NotNull(zapret.ValidateFolder(Path.Combine(_root, "missing")));
    }

    [Theory]
    [InlineData("A network error occurred. Check your internet connection and retry.", true)]
    [InlineData("YouTube took too long to answer. Try again.", true)]
    [InlineData("This video is private.", false)]
    [InlineData(null, false)]
    public void Recognizes_blocking_errors(string? error, bool expected) =>
        Assert.Equal(expected, _sp.GetRequiredService<IZapretService>().LooksLikeBlocking(error));

    [SkippableFact]
    public async Task Finds_real_installation_and_checks_youtube()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("SONIC_INTEGRATION") == "1");
        var zapret = _sp.GetRequiredService<IZapretService>();
        var status = zapret.GetStatus();
        output.WriteLine($"Installed={status.Installed} Running={status.Running} Folder={status.Folder} Strategies={status.Strategies.Count} Service={status.ServiceInstalled}");
        var reach = await zapret.CheckYouTubeAsync();
        output.WriteLine(reach.Details);
    }
}
