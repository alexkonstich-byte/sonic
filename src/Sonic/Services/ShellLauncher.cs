using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sonic.Services;

/// <summary>Opens files, folders and links with the Windows shell.</summary>
public interface IShellLauncher
{
    bool OpenFile(string path);
    bool RevealInExplorer(string path);
    bool OpenFolder(string directory);
    bool OpenUri(string uri);
}

public sealed class ShellLauncher(ILogger<ShellLauncher> logger, IInAppNotificationService notifications) : IShellLauncher
{
    public bool OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            notifications.ShowWarning("File not found", $"\"{Path.GetFileName(path)}\" no longer exists. It may have been moved or deleted.");
            return false;
        }

        return Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public bool RevealInExplorer(string path)
    {
        if (File.Exists(path)) return Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir)) return OpenFolder(dir);
        notifications.ShowWarning("File not found", "The file and its folder no longer exist.");
        return false;
    }

    public bool OpenFolder(string directory)
    {
        if (!Directory.Exists(directory))
        {
            notifications.ShowWarning("Folder not found", $"\"{directory}\" doesn't exist yet.");
            return false;
        }

        return Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = false });
    }

    public bool OpenUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
            return false;
        return Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
    }

    private bool Start(ProcessStartInfo psi)
    {
        try
        {
            using var _ = Process.Start(psi);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not launch {File}", psi.FileName);
            notifications.ShowError("Couldn't open", ex.Message);
            return false;
        }
    }
}
