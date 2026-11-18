namespace Sonic.Core.Abstractions;

/// <summary>Well-known application data locations.</summary>
public interface IAppPaths
{
    string DataDirectory { get; }
    string ToolsDirectory { get; }
    string TempDirectory { get; }
    string ArtworkCacheDirectory { get; }
    string LogsDirectory { get; }
    string DatabasePath { get; }
    string SettingsPath { get; }
}
