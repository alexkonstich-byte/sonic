using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;
using Sonic.Services;
using Sonic.ViewModels;

namespace Sonic;

public sealed partial class MainWindow : Window
{
    private readonly INavigationService _navigation;
    private readonly IDownloadQueueService _queue;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILogger<MainWindow> _logger;
    private bool _syncingSelection;
    private bool _closeConfirmed;
    private bool _closing;
    private bool _trayHintShown;

    public MainWindow()
    {
        ViewModel = App.GetService<ShellViewModel>();
        _navigation = App.GetService<INavigationService>();
        _queue = App.GetService<IDownloadQueueService>();
        _settings = App.GetService<ISettingsService>();
        _dialogs = App.GetService<IDialogService>();
        _logger = App.GetService<ILogger<MainWindow>>();
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Sonic.ico"));
        ConfigureSize();

        _navigation.Initialize(ContentFrame);
        _navigation.Navigated += OnNavigated;
        AppWindow.Closing += OnClosing;
    }

    public ShellViewModel ViewModel { get; }

    public PlayerViewModel Player { get; } = App.GetService<PlayerViewModel>();

    public void NavigateToStartPage(StartupPage page) => _navigation.NavigateTo(page switch
    {
        StartupPage.Downloads => PageKey.Downloads,
        StartupPage.Library => PageKey.Library,
        _ => PageKey.Home,
    });

    private void ConfigureSize()
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(720 * scale);
            presenter.PreferredMinimumHeight = (int)(520 * scale);
        }

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(1320 * scale), (int)(area.Width * 0.9));
        var height = Math.Min((int)(860 * scale), (int)(area.Height * 0.9));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection) return;
        if (args.IsSettingsSelected)
        {
            _navigation.NavigateTo(PageKey.Settings);
            return;
        }

        if (args.SelectedItemContainer?.Tag is string tag && Enum.TryParse<PageKey>(tag, out var key))
            _navigation.NavigateTo(key);
    }

    private void OnNavigated(object? sender, PageKey page)
    {
        AppTitleBar.IsBackButtonEnabled = _navigation.CanGoBack;
        var tag = page == PageKey.PlaylistDetail ? nameof(PageKey.Playlists) : page.ToString();
        _syncingSelection = true;
        try
        {
            NavView.SelectedItem = page == PageKey.Settings
                ? NavView.SettingsItem
                : NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnBackRequested(TitleBar sender, object args) => _navigation.GoBack();

    private void OnPaneToggleRequested(TitleBar sender, object args) => NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void OnNotificationClosed(InfoBar sender, object args)
    {
        if (sender.DataContext is InAppNotification n) ViewModel.DismissNotificationCommand.Execute(n);
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed) return;
        args.Cancel = true;

        // "Close to tray": keep running (downloads continue) unless Exit was chosen in the tray menu.
        var windows = App.GetService<IWindowService>();
        if (_settings.Current.MinimizeToTray && !windows.ExitRequested)
        {
            AppWindow.Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                App.GetService<IToastService>().ShowAlways("Sonic is still running", "Downloads continue in the background. Use the tray icon to open Sonic or exit.");
            }

            return;
        }

        if (_closing) return;
        _closing = true;
        try
        {
            var running = _queue.Items.Count(i => i.Status.IsActive());
            if (running > 0 && _settings.Current.ConfirmExitWithActiveDownloads)
            {
                var quit = await _dialogs.ConfirmAsync(
                    "Downloads in progress",
                    $"{(running == 1 ? "1 download is" : $"{running} downloads are")} still running. If you quit now, they'll resume from where they stopped the next time you open Sonic.",
                    "Quit",
                    closeText: "Keep downloading");
                if (!quit) return;
            }

            await App.ShutdownAsync();
            _closeConfirmed = true;
            Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during shutdown");
            _closeConfirmed = true;
            Close();
        }
        finally
        {
            _closing = false;
        }
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);
}
