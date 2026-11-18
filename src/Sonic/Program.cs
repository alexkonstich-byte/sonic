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

    private static bool RedirectToExistingInstance()
    {
        var mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (mainInstance.IsCurrent)
        {
            // A second launch (or a sonic:// link, or a toast) is forwarded here.
            mainInstance.Activated += (_, e) => App.OnRedirectedActivation(e);
            return false;
        }

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        // Redirect on a worker thread: blocking the STA thread on this call can deadlock COM.
        Task.Run(() => mainInstance.RedirectActivationToAsync(activation).AsTask()).Wait(TimeSpan.FromSeconds(10));
        return true;
    }
}
