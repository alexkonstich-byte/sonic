using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Sonic.ViewModels;

namespace Sonic.Views;

public sealed partial class PlaylistDetailPage : Page
{
    public PlaylistDetailPage()
    {
        ViewModel = App.GetService<PlaylistDetailViewModel>();
        InitializeComponent();
    }

    public PlaylistDetailViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.OnNavigatedToAsync(e.Parameter);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.OnNavigatedFrom();
    }

    private static PlaylistEntryViewModel? EntryOf(object sender) => (sender as FrameworkElement)?.DataContext as PlaylistEntryViewModel;

    /// <summary>Reorder inside the list, or drop the song file into another app.</summary>
    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e) =>
        Sonic.Helpers.FileDrag.Attach(e, e.Items.OfType<PlaylistEntryViewModel>().Select(i => i.Model.Track?.FilePath), keepMove: true);

    private async void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => await ViewModel.PersistOrderAsync();

    private void OnEntryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (EntryOf(sender) is { IsAvailable: true } entry) ViewModel.PlayEntryCommand.Execute(entry);
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) ViewModel.PlayEntryCommand.Execute(entry);
    }

    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) ViewModel.RevealEntryCommand.Execute(entry);
    }

    private void OnFindSourceClick(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) ViewModel.FindSourceCommand.Execute(entry);
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) ViewModel.RemoveEntryCommand.Execute(entry);
    }
}
