using Sonic.Core.Helpers;
using Sonic.Core.Models;

namespace Sonic.Core.Tests;

public class QolTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42", "youtube:dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc", "youtube:dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ&list=RD", "youtube:dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/shorts/dQw4w9WgXcQ", "youtube:dQw4w9WgXcQ")]
    [InlineData("https://rutube.ru/video/abc/?r=wd", "rutube.ru/video/abc")]
    [InlineData("HTTPS://SoundCloud.com/Artist/Track/", "soundcloud.com/artist/track")]
    public void Canonical_keys_match_url_variants(string url, string key) =>
        Assert.Equal(key, SourceDetector.CanonicalKey(url));

    [Fact]
    public void Default_presets_are_valid()
    {
        var presets = DownloadPreset.Defaults();
        Assert.True(presets.Count >= 4);
        Assert.All(presets, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
        Assert.All(presets.Where(p => p.Kind == MediaKind.Video), p => Assert.NotNull(p.Video));
        Assert.Equal(presets.Count, presets.Select(p => p.Id).Distinct().Count());
    }
}
