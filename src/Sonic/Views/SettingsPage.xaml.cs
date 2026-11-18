using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Sonic.ViewModels;

namespace Sonic.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.OnNavigatedToAsync(e.Parameter);
        if (ViewModel.FocusSection == "cookies") CookiesExpander.IsExpanded = true;
        if (ViewModel.FocusSection == "zapret") ZapretExpander.IsExpanded = true;
        var target = ViewModel.FocusSection switch
        {
            "zapret" => (FrameworkElement)ZapretExpander,
            "cookies" => (FrameworkElement)CookiesExpander,
            "integrations" => IntegrationsHeader,
            "downloads" => DownloadsHeader,
            "video" => VideoHeader,
            "library" => LibraryHeader,
            _ => null,
        };
        if (target is not null)
        {
            // Wait for layout so the section can be scrolled into view.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => target.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = true }));
        }
    }

    private void OnRemoveFolderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string folder) ViewModel.RemoveMusicFolderCommand.Execute(folder);
    }

    private void OnDeletePresetClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if ((sender as Microsoft.UI.Xaml.FrameworkElement)?.Tag is Sonic.Core.Models.DownloadPreset preset) ViewModel.DeletePresetCommand.Execute(preset);
    }

    private void OnRemoveIgnoredAppClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Sonic.Services.SteamApp app) ViewModel.RemoveIgnoredAppCommand.Execute(app);
    }
}
