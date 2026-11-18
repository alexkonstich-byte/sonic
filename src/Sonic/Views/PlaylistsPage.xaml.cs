using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Sonic.ViewModels;

namespace Sonic.Views;

public sealed partial class PlaylistsPage : Page
{
    public PlaylistsPage()
    {
        ViewModel = App.GetService<PlaylistsViewModel>();
        InitializeComponent();
    }

    public PlaylistsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.OnNavigatedToAsync(e.Parameter);
    }

    private void OnPlaylistClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistCardViewModel playlist) ViewModel.OpenCommand.Execute(playlist);
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PlaylistCardViewModel playlist) ViewModel.RenameCommand.Execute(playlist);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PlaylistCardViewModel playlist) ViewModel.DeleteCommand.Execute(playlist);
    }
}
