using Sonic.Core.Helpers;
using Sonic.Core.Models;

namespace Sonic.Core.Tests;

public class NetworkOptionsTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("socks5://user:pass@proxy.local:1080", true)]
    [InlineData("127.0.0.1:8080", false)]
    [InlineData("ftp://host:21", false)]
    public void Validates_proxies(string? proxy, bool valid) =>
        Assert.Equal(valid, NetworkOptions.ValidateProxy(proxy) is null);

    [Fact]
    public void Adds_proxy_to_every_run()
    {
        var args = NetworkOptions.CommonArguments(new AppSettings { ProxyUrl = "socks5://127.0.0.1:1080" }).ToList();
        Assert.Equal(["--proxy", "socks5://127.0.0.1:1080"], args);
        Assert.Empty(NetworkOptions.CommonArguments(new AppSettings()));
    }

    [Fact]
    public void Download_arguments_include_speed_limit_and_sponsorblock()
    {
        var s = new AppSettings { SpeedLimitKBps = 2048, SponsorBlockAudio = true, SponsorBlockVideo = SponsorBlockMode.Mark };
        Assert.Equal(["--limit-rate", "2048K", "--sponsorblock-remove", "music_offtopic"], NetworkOptions.DownloadArguments(s, MediaKind.Audio));
        Assert.Equal(["--limit-rate", "2048K", "--sponsorblock-mark", NetworkOptions.VideoSponsorCategories], NetworkOptions.DownloadArguments(s, MediaKind.Video));
        Assert.Empty(NetworkOptions.DownloadArguments(new AppSettings { SponsorBlockAudio = false }, MediaKind.Audio));
    }
}
