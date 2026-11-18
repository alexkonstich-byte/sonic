using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Windows.Management.Deployment;

namespace Sonic.Services;

/// <summary>
/// Puts Sonic in the Windows Share menu by registering the signed identity package that ships in the install
/// folder (identity\Sonic.Identity.msix). The package's only app is SonicShare.exe, which forwards shared links to
/// Sonic.exe; Sonic.exe itself never carries the identity, so a broken package can't stop it from starting. The package is self-signed, so its certificate is added once to the
/// machine's Trusted People store (one administrator prompt). Only the installed version supports this.
/// </summary>
public sealed partial class ShareIntegrationService : ObservableObject
{
    public const string PackageName = "Sonic.Identity";

    private readonly IInAppNotificationService _notifications;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ShareIntegrationService> _logger;

    public ShareIntegrationService(IInAppNotificationService notifications, IDialogService dialogs, ILogger<ShareIntegrationService> logger)
    {
        _notifications = notifications;
        _dialogs = dialogs;
        _logger = logger;
        Refresh();
    }

    private static string IdentityDirectory => Path.Combine(AppContext.BaseDirectory, "identity");
    private static string PackagePath => Path.Combine(IdentityDirectory, "Sonic.Identity.msix");
    private static string CertificatePath => Path.Combine(IdentityDirectory, "Sonic.Identity.cer");

    /// <summary>The identity package exists only in installed release builds.</summary>
    public bool IsAvailable => File.Exists(PackagePath) && File.Exists(CertificatePath) &&
                               File.Exists(Path.Combine(AppContext.BaseDirectory, "SonicShare.exe")) && App.GetService<UpdateService>().IsInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanEnable), nameof(CanDisable))]
    [NotifyCanExecuteChangedFor(nameof(EnableCommand), nameof(DisableCommand))]
    private bool _isRegistered;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEnable), nameof(CanDisable))]
    [NotifyCanExecuteChangedFor(nameof(EnableCommand), nameof(DisableCommand))]
    private bool _isBusy;

    public bool CanEnable => IsAvailable && !IsRegistered && !IsBusy;
    public bool CanDisable => IsRegistered && !IsBusy;

    public string StatusText => !IsAvailable
        ? "Available in the installed version of Sonic (Setup.exe)."
        : !IsRegistered ? "Share a link from a browser or another app straight to Sonic. Needs one administrator prompt."
        : "On. Choose Share in a browser or app, then Sonic.";

    private static Windows.ApplicationModel.Package? FindPackage()
    {
        try
        {
            return new PackageManager().FindPackagesForUser(string.Empty).FirstOrDefault(p => p.Id.Name == PackageName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Refresh() => IsRegistered = FindPackage() is not null;

    /// <summary>After an app update: re-register the newer identity package so it points at the new version.</summary>
    public async Task UpdateRegistrationAsync()
    {
        if (!IsAvailable || FindPackage() is not { } package) return;
        var current = AppVersion();
        var registered = package.Id.Version;
        if (current is null || (registered.Major, registered.Minor, registered.Build) == (current.Major, current.Minor, current.Build)) return;
        try
        {
            await RegisterAsync();
            _logger.LogInformation("Identity package updated to {Version}", current);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Updating the identity package failed");
        }
    }

    private static Version? AppVersion() => typeof(ShareIntegrationService).Assembly.GetName().Version;

    [RelayCommand(CanExecute = nameof(CanEnable))]
    private async Task EnableAsync()
    {
        IsBusy = true;
        try
        {
            using var cert = X509CertificateLoader.LoadCertificateFromFile(CertificatePath);
            if (!IsTrusted(cert))
            {
                if (!await _dialogs.ConfirmAsync("Allow Sonic in the Share menu?",
                        "Windows only lists signed apps in the Share menu. Sonic's identity package is signed with its own certificate, " +
                        "which will be added to this computer's Trusted People store. Windows asks for administrator permission once.",
                        "Continue"))
                    return;

                if (!await RunElevatedAsync("certutil.exe", $"-addstore TrustedPeople \"{CertificatePath}\"") || !IsTrusted(cert))
                {
                    _notifications.ShowWarning("Not enabled", "The certificate wasn't added (the administrator prompt was declined or failed).");
                    return;
                }
            }

            await RegisterAsync();
            Refresh();
            _notifications.ShowSuccess("Sonic is in the Share menu", "Share › Sonic in a browser or app now opens the link here.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registering the identity package failed");
            await _dialogs.ShowErrorAsync("Couldn't add Sonic to the Share menu", ex.Message, ex.ToString());
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisable))]
    private async Task DisableAsync()
    {
        IsBusy = true;
        try
        {
            if (FindPackage() is { } package)
            {
                var result = await new PackageManager().RemovePackageAsync(package.Id.FullName);
                if (result.ExtendedErrorCode is { } error) throw new InvalidOperationException(result.ErrorText, error);
            }

            Refresh();
            // Offer to remove the certificate too; Sonic doesn't need it without the Share menu.
            if (File.Exists(CertificatePath))
            {
                using var cert = X509CertificateLoader.LoadCertificateFromFile(CertificatePath);
                if (IsTrusted(cert) && await _dialogs.ConfirmAsync("Remove the certificate too?",
                        "Sonic's signing certificate is no longer needed. Removing it asks for administrator permission.", "Remove", closeText: "Keep"))
                    await RunElevatedAsync("certutil.exe", $"-delstore TrustedPeople {cert.Thumbprint}");
            }

            _notifications.ShowInfo("Removed from the Share menu", "Sonic no longer appears when sharing.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Removing the identity package failed");
            await _dialogs.ShowErrorAsync("Couldn't remove Sonic from the Share menu", ex.Message, ex.ToString());
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static async Task RegisterAsync()
    {
        var options = new AddPackageOptions
        {
            ExternalLocationUri = new Uri(AppContext.BaseDirectory),
            ForceUpdateFromAnyVersion = true,
            // Don't kill the running Sonic while (re-)registering its identity.
            ForceAppShutdown = false,
            DeferRegistrationWhenPackagesAreInUse = true,
        };
        var result = await new PackageManager().AddPackageByUriAsync(new Uri(PackagePath), options);
        if (result.ExtendedErrorCode is { } error) throw new InvalidOperationException(result.ErrorText, error);
    }

    /// <summary>Removes the identity package (used by the uninstaller hook). Never throws.</summary>
    public static void RemoveRegistration()
    {
        try
        {
            if (FindPackage() is { } package) new PackageManager().RemovePackageAsync(package.Id.FullName).AsTask().Wait(TimeSpan.FromSeconds(30));
        }
        catch (Exception)
        {
        }
    }

    private static bool IsTrusted(X509Certificate2 cert)
    {
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false).Count > 0;
    }

    private async Task<bool> RunElevatedAsync(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            if (process is null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // ERROR_CANCELLED: the user declined the UAC prompt.
            _logger.LogInformation(ex, "Elevated {File} was not run", file);
            return false;
        }
    }
}
