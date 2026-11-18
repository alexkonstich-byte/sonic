using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Sonic.Services;

/// <summary>Registers Sonic to start when the user signs in (per-user Run key; no admin rights needed).</summary>
public interface IStartupService
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

public sealed class StartupService(ILogger<StartupService> logger) : IStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Sonic";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(ValueName, Command);
            else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            logger.LogError(ex, "Could not update startup registration");
            throw new InvalidOperationException("Windows didn't allow Sonic to change its startup setting.", ex);
        }
    }
}
