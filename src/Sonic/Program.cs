using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Sonic;

/// <summary>
/// Custom entry point: enforces a single running instance (the library database and download queue are
/// owned by one process) and forwards later launches to the existing window.
/// </summary>
public static class Program
{
    private const string InstanceKey = "Sonic.MainInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        // Velopack install/update/uninstall hooks run (and exit) here before anything else starts.
        Velopack.VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => RemoveSystemIntegration())
            .Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();

        PendingCommand = ReadShareActivation();
        if (RedirectToExistingInstance()) return 0;

        Application.Start(static _ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }

    /// <summary>On uninstall: remove the per-user registrations Sonic made (sign-in launch, sonic:// links).</summary>
    private static void RemoveSystemIntegration()
    {
        try
        {
            using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
                if (run?.GetValue("Sonic") is not null) run.DeleteValue("Sonic");
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\sonic", throwOnMissingSubKey: false);
            Services.ShareIntegrationService.RemoveRegistration();
        }
        catch (Exception)
        {
            // Never block the uninstaller.
        }
    }

    /// <summary>A sonic:// command from a Share activation, handled after startup.</summary>
    public static string? PendingCommand { get; private set; }

    /// <summary>
    /// Started from the Windows Share menu: read the shared link, close the share UI and turn it into a
    /// sonic://open command (which opens the link on the Home page).
    /// </summary>
    private static string? ReadShareActivation()
    {
        try
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind != ExtendedActivationKind.ShareTarget ||
                activation.Data is not Windows.ApplicationModel.Activation.IShareTargetActivatedEventArgs share) return null;

            var operation = share.ShareOperation;
            var link = Task.Run(async () =>
            {
                var data = operation.Data;
                if (data.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.WebLink))
                    return (await data.GetWebLinkAsync()).AbsoluteUri;
                if (data.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    var text = await data.GetTextAsync();
                    var match = System.Text.RegularExpressions.Regex.Match(text, @"https?://\S+");
                    return match.Success ? match.Value : null;
                }

                return null;
            }).GetAwaiter().GetResult();
            operation.ReportCompleted();
            return link is null ? "sonic://show" : "sonic://open?url=" + Uri.EscapeDataString(link);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool RedirectToExistingInstance()
    {
        var mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (mainInstance.IsCurrent)
        {
            // A second launch (or a sonic:// link, or a toast) is forwarded here.
            mainInstance.Activated += (_, e) => App.OnRedirectedActivation(e);
            return false;
        }

        // A share is forwarded as a normal launch with the sonic:// command (share data can't be redirected).
        if (PendingCommand is { } command && Environment.ProcessPath is { } exe)
        {
            Process.Start(new ProcessStartInfo(exe, $"\"{command}\"") { UseShellExecute = false });
            return true;
        }

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        // Redirect on a worker thread: blocking the STA thread on this call can deadlock COM.
        Task.Run(() => mainInstance.RedirectActivationToAsync(activation).AsTask()).Wait(TimeSpan.FromSeconds(10));
        return true;
    }
}
