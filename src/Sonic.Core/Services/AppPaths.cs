using Sonic.Core.Abstractions;

namespace Sonic.Core.Services;

public sealed class AppPaths : IAppPaths
{
    public AppPaths()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sonic"))
    {
    }

    public AppPaths(string root)
    {
        DataDirectory = root;
        ToolsDirectory = Path.Combine(root, "tools");
        TempDirectory = Path.Combine(root, "temp");
        ArtworkCacheDirectory = Path.Combine(root, "artwork");
        LogsDirectory = Path.Combine(root, "logs");
        DatabasePath = Path.Combine(root, "sonic.db");
        SettingsPath = Path.Combine(root, "settings.json");

        foreach (var dir in new[] { DataDirectory, ToolsDirectory, TempDirectory, ArtworkCacheDirectory, LogsDirectory })
            Directory.CreateDirectory(dir);
    }

    public string DataDirectory { get; }
    public string ToolsDirectory { get; }
    public string TempDirectory { get; }
    public string ArtworkCacheDirectory { get; }
    public string LogsDirectory { get; }
    public string DatabasePath { get; }
    public string SettingsPath { get; }
}
