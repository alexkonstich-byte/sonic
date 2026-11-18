using Sonic.Core.Helpers;
using Sonic.Core.Models;
using Sonic.Core.Services;

namespace Sonic.Core.Tests;

public class SettingsMigrationTests
{
    [Fact]
    public void Defaults_point_to_the_downloads_folder()
    {
        var s = new AppSettings();
        Assert.StartsWith(KnownFolders.Downloads, s.DownloadDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(KnownFolders.Downloads, s.VideoDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(KnownFolders.Downloads));
    }

    [Fact]
    public void Old_default_folders_move_to_downloads()
    {
        var oldMusic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Sonic");
        var oldVideo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Sonic");
        var s = new AppSettings { DownloadDirectory = oldMusic, VideoDirectory = oldVideo };

        Assert.True(SettingsService.Migrate(s, "{ \"DownloadDirectory\": \"x\" }")); // no SettingsVersion = v1
        Assert.Equal(AppSettings.DefaultAudioDirectory, s.DownloadDirectory);
        Assert.Equal(AppSettings.DefaultVideoDirectory, s.VideoDirectory);
        Assert.Equal(AppSettings.CurrentVersion, s.SettingsVersion);
        if (Directory.Exists(oldMusic)) Assert.Contains(oldMusic, s.MusicFolders); // old downloads stay in the library
    }

    [Fact]
    public void Custom_folders_are_kept()
    {
        var s = new AppSettings { DownloadDirectory = @"D:\My Music", VideoDirectory = @"D:\Clips" };
        SettingsService.Migrate(s, "{}");
        Assert.Equal(@"D:\My Music", s.DownloadDirectory);
        Assert.Equal(@"D:\Clips", s.VideoDirectory);
    }

    [Fact]
    public void Current_settings_are_not_migrated_again()
    {
        var s = new AppSettings();
        Assert.False(SettingsService.Migrate(s, "{ \"SettingsVersion\": 2 }"));
    }
}
