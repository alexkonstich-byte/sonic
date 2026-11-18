using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Sonic.ViewModels;

namespace Sonic.Views;

public sealed partial class YouTubePage : Page
{
    public YouTubePage()
    {
        ViewModel = App.GetService<YouTubeViewModel>();
        InitializeComponent();
    }

    public YouTubeViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.OnNavigatedToAsync(e.Parameter);
    }

    private async void OnFeedChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string key) await ViewModel.SelectFeedAsync(key);
    }

    private static FeedItemViewModel? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as FeedItemViewModel;

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } i) ViewModel.PreviewCommand.Execute(i);
    }

    private void OnAudioClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } i) ViewModel.DownloadAudioCommand.Execute(i);
    }

    private void OnVideoClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } i) ViewModel.DownloadVideoCommand.Execute(i);
    }

    private void OnOptionsClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } i) ViewModel.MoreOptionsCommand.Execute(i);
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } i) ViewModel.OpenOnYouTubeCommand.Execute(i);
    }
}
