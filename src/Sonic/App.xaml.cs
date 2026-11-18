using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Sonic.Core;
using Sonic.Core.Abstractions;
using Sonic.Core.Data;
using Sonic.Core.Models;
using Sonic.Core.Services;
using Sonic.Services;
using Sonic.ViewModels;

namespace Sonic;

public partial class App : Application
{
    private static IHost? _host;
    private static MainWindow? _window;
    private static int _shutdownStarted;
    private readonly LoggingLevelSwitch _levelSwitch = new(LogEventLevel.Information);

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    public static IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("The application host is not running.");

    public static T GetService<T>() where T : notnull => Services.GetRequiredService<T>();

    public static void BringToForeground()
    {
        if (_window is null) return;
        _window.DispatcherQueue.TryEnqueue(() => GetService<IWindowService>().BringToFront());
    }

    /// <summary>Called when another launch (e.g. a sonic:// link opened by the browser) is redirected here.</summary>
    public static void OnRedirectedActivation(Microsoft.Windows.AppLifecycle.AppActivationArguments e)
    {
        string? arguments = e.Data switch
        {
            Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch => launch.Arguments,
            Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocol => protocol.Uri?.AbsoluteUri,
            _ => null,
        };
        if (_host is null) return;
        if (arguments?.Contains("sonic:", StringComparison.OrdinalIgnoreCase) == true)
            _ = GetService<IActivationService>().HandleArgumentsAsync(arguments);
        else
            BringToForeground();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var paths = new AppPaths();
            ConfigureLogging(paths);
            Log.Information("Sonic starting ({Version})", ShellViewModel.AppVersion);

            _host = BuildHost(paths);
            await _host.StartAsync();

            var settings = GetService<ISettingsService>();
            await settings.LoadAsync();
            _levelSwitch.MinimumLevel = settings.Current.VerboseLogging ? LogEventLevel.Debug : LogEventLevel.Information;
            await GetService<IDatabaseInitializer>().InitializeAsync();

            // Create stores before the queue loads so they receive the initial items.
            _ = GetService<DownloadsStore>();
            _ = GetService<ToolsStore>();

            _window = new MainWindow();
            var windows = GetService<IWindowService>();
            windows.Attach(_window);
            GetService<IThemeService>().Apply(settings.Current.Theme);
            _window.NavigateToStartPage(settings.Current.StartupPage);
            _window.Activate();

            GetService<IToastService>().Initialize();
            HookDownloadNotifications();
            StartDesktopIntegration(settings.Current);
            _ = StartBackgroundServicesAsync(settings.Current);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            await Log.CloseAndFlushAsync();
            MessageBox(IntPtr.Zero, $"Sonic couldn't start.\n\n{ex.Message}\n\nDetails were written to the log file.", "Sonic", 0x10);
            Exit();
        }
    }

    private void ConfigureLogging(IAppPaths paths)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(_levelSwitch)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Extensions.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "sonic-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private IHost BuildHost(IAppPaths paths)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true,
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(dispose: false);

        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        builder.Services.AddSonicCore($"Sonic/{version} (Windows)");
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(_levelSwitch);

        // UI services
        builder.Services.AddSingleton<IUiDispatcher>(new UiDispatcher(DispatcherQueue.GetForCurrentThread()));
        builder.Services.AddSingleton<IWindowService, WindowService>();
        builder.Services.AddSingleton<INavigationService, NavigationService>();
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<IThemeService, ThemeService>();
        builder.Services.AddSingleton<IInAppNotificationService, InAppNotificationService>();
        builder.Services.AddSingleton<IToastService, ToastService>();
        builder.Services.AddSingleton<IStartupService, StartupService>();
        builder.Services.AddSingleton<IShellLauncher, ShellLauncher>();
        builder.Services.AddSingleton<IActivationService, ActivationService>();
        builder.Services.AddSingleton<IClipboardWatcher, ClipboardWatcher>();
        builder.Services.AddSingleton<ITrayService, TrayService>();
        builder.Services.AddSingleton<IZapretAdvisor, ZapretAdvisor>();
        builder.Services.AddSingleton<IToolSetupService, ToolSetupService>();
        builder.Services.AddSingleton<GameModeService>();
        builder.Services.AddSingleton<TaskbarProgressService>();
        builder.Services.AddSingleton<AutoRetryService>();
        builder.Services.AddSingleton<DiscordPresenceService>();
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddSingleton<ShareIntegrationService>();

        // View models: page view models are singletons so state survives navigation.
        builder.Services.AddSingleton<DownloadsStore>();
        builder.Services.AddSingleton<ToolsStore>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<PlayerViewModel>();
        builder.Services.AddSingleton<HomeViewModel>();
        builder.Services.AddSingleton<DownloadsViewModel>();
        builder.Services.AddSingleton<LibraryViewModel>();
        builder.Services.AddSingleton<PlaylistsViewModel>();
        builder.Services.AddSingleton<HistoryViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<YouTubeViewModel>();
        builder.Services.AddTransient<PlaylistDetailViewModel>();
        builder.Services.AddTransient<MetadataEditorViewModel>();
        builder.Services.AddTransient<SpotifyImportViewModel>();
        builder.Services.AddTransient<SourceSearchViewModel>();
        builder.Services.AddTransient<ToolInstallViewModel>();
        builder.Services.AddTransient<BatchDownloadViewModel>();
        builder.Services.AddTransient<PlaylistDownloadViewModel>();
        return builder.Build();
    }

    private static void StartDesktopIntegration(AppSettings settings)
    {
        try
        {
            var tray = GetService<ITrayService>();
            tray.Initialize();
            var store = GetService<DownloadsStore>();
            store.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DownloadsStore.ActiveCount))
                    tray.SetTooltip(store.ActiveCount == 0 ? "Sonic" : $"Sonic — {ShellViewModel.ActiveDownloadsText(store.ActiveCount)}");
            };

            GetService<IClipboardWatcher>().Start();
            GetService<TaskbarProgressService>().Start();
            GetService<AutoRetryService>().Start();
            GetService<DiscordPresenceService>().Start();
            GetService<UpdateService>().Start();
            ProtocolRegistration.Apply(settings.ProtocolHandler);
            GetService<ISettingsService>().Changed += (_, s) =>
            {
                GetService<IUiDispatcher>().Post(tray.ApplySettings);
                ProtocolRegistration.Apply(s.ProtocolHandler);
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Desktop integration failed to start");
        }
    }

    private static async Task StartBackgroundServicesAsync(AppSettings settings)
    {
        var logger = GetService<ILogger<App>>();
        var notifications = GetService<IInAppNotificationService>();
        try
        {
            await GetService<IUiDispatcher>().RunAsync(() => GetService<IZapretAdvisor>().AutoStartAsync());
            var dependencies = GetService<IDependencyService>();
            await Task.Run(() => dependencies.RefreshAsync(settings.CheckForToolUpdates));
            // Installs missing tools automatically and fetches zapret only if YouTube turns out to be blocked.
            _ = GetService<IUiDispatcher>().RunAsync(() => GetService<IToolSetupService>().RunAsync());

            await Task.Run(() => GetService<IDownloadQueueService>().InitializeAsync());
            GetService<GameModeService>().Start();

            // Launched by a sonic:// link while Sonic wasn't running.
            await GetService<IActivationService>().HandleArgumentsAsync(string.Join(' ', Environment.GetCommandLineArgs().Skip(1)));
            await GetService<ShareIntegrationService>().UpdateRegistrationAsync();

            var library = GetService<ILibraryService>();
            library.ConfigureWatchers();
            if (settings.ScanOnStartup) await Task.Run(() => library.ScanAsync(null));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background startup failed");
            notifications.ShowError("Startup problem", ex.Message);
        }
    }

    private static void HookDownloadNotifications()
    {
        var queue = GetService<IDownloadQueueService>();
        var toast = GetService<IToastService>();
        var windows = GetService<IWindowService>();
        var notifications = GetService<IInAppNotificationService>();
        var dispatcher = GetService<IUiDispatcher>();
        queue.DownloadFinished += (_, item) => dispatcher.Post(() =>
        {
            var title = item.Title ?? item.Url;
            var foreground = windows.IsForeground;
            switch (item.Status)
            {
                case DownloadStatus.Completed:
                    if (!foreground) toast.Show("Download complete", title);
                    break;
                case DownloadStatus.Failed:
                    // Network-looking YouTube failures get a "Start zapret" suggestion instead of a plain error.
                    if (GetService<IZapretAdvisor>().MaybeSuggest(item.ErrorMessage, item.Url)) break;
                    if (!foreground) toast.Show("Download failed", $"{title}: {item.ErrorMessage}");
                    else notifications.ShowError("Download failed", $"{title}: {item.ErrorMessage}");
                    break;
            }
        });
    }

    /// <summary>Stops downloads (keeping partial data for resume), flushes state and logs.</summary>
    public static async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 1 || _host is null) return;
        Log.Information("Sonic shutting down");
        try
        {
            await GetService<IDownloadQueueService>().ShutdownAsync();
            GetService<ILibraryService>().Dispose();
            GetService<IToastService>().Shutdown();
            GetService<ITrayService>().Dispose();
            GetService<GameModeService>().Dispose();
            await GetService<DiscordPresenceService>().DisposeAsync();
            GetService<UpdateService>().ApplyOnExit();
            await _host.StopAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Shutdown error");
        }
        finally
        {
            _host.Dispose();
            await Log.CloseAndFlushAsync();
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        // Keep the app alive for recoverable UI errors and tell the user instead of crashing silently.
        e.Handled = true;
        try
        {
            GetService<IInAppNotificationService>().ShowError("Something went wrong", e.Exception.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not report unhandled exception");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
