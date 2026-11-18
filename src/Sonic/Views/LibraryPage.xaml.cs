using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Sonic.ViewModels;

namespace Sonic.Views;

public sealed partial class LibraryPage : Page
{
    private TrackViewModel? _menuTrack;

    public LibraryPage()
    {
        ViewModel = App.GetService<LibraryViewModel>();
        InitializeComponent();
    }

    public LibraryViewModel ViewModel { get; }

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

    private void OnViewSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag) ViewModel.ShowViewCommand.Execute(tag);
        TrackList.SelectedItems.Clear();
    }

    /// <summary>Drag tracks out to Explorer, messengers and other apps.</summary>
    private void OnTrackDragStarting(object sender, DragItemsStartingEventArgs e) =>
        Sonic.Helpers.FileDrag.Attach(e, e.Items.OfType<TrackViewModel>().Select(t => t.Model.FilePath));

    private void OnTrackSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.UpdateSelection(TrackList.SelectedItems.OfType<TrackViewModel>());

    private void OnTrackDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TrackViewModel track) ViewModel.PlayCommand.Execute(track);
    }

    private async void OnTrackMenuOpening(object sender, object e)
    {
        var flyout = (MenuFlyout)sender;
        _menuTrack = (flyout.Target as FrameworkElement)?.DataContext as TrackViewModel;
        if (flyout.Items.OfType<MenuFlyoutItem>().FirstOrDefault(i => i.Text is "Favorite" or "Remove from favorites") is { } fav)
            fav.Text = _menuTrack?.IsFavorite == true ? "Remove from favorites" : "Favorite";
        await PopulatePlaylistMenuAsync(AddToPlaylistMenu.Items, () => _menuTrack);
    }

    private async void OnSelectionPlaylistMenuOpening(object sender, object e) =>
        await PopulatePlaylistMenuAsync(SelectionPlaylistMenu.Items, () => null);

    private async Task PopulatePlaylistMenuAsync(IList<MenuFlyoutItemBase> items, Func<TrackViewModel?> target)
    {
        items.Clear();
        var newItem = new MenuFlyoutItem { Text = "New playlist…", Icon = new FontIcon { Glyph = "" } };
        newItem.Click += (_, _) => ViewModel.AddToNewPlaylistCommand.Execute(target());
        items.Add(newItem);

        var playlists = await ViewModel.GetLocalPlaylistsAsync();
        if (playlists.Count > 0) items.Add(new MenuFlyoutSeparator());
        foreach (var p in playlists)
        {
            var item = new MenuFlyoutItem { Text = p.Name };
            item.Click += async (_, _) => await ViewModel.AddToPlaylistAsync(p.Id, p.Name, target());
            items.Add(item);
        }
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_menuTrack is not null) ViewModel.PlayCommand.Execute(_menuTrack);
    }

    private void OnQueueClick(object sender, RoutedEventArgs e) => ViewModel.AddToQueueCommand.Execute(_menuTrack);

    private void OnExternalClick(object sender, RoutedEventArgs e)
    {
        if (_menuTrack is not null) ViewModel.OpenExternalCommand.Execute(_menuTrack);
    }

    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        if (_menuTrack is not null) ViewModel.OpenLocationCommand.Execute(_menuTrack);
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (_menuTrack is not null) ViewModel.EditMetadataCommand.Execute(_menuTrack);
    }

    private void OnFindCoversClick(object sender, RoutedEventArgs e)
    {
        if (_menuTrack is not null) ViewModel.FindCoversCommand.Execute(_menuTrack);
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (_menuTrack is not null) ViewModel.ToggleFavoriteCommand.Execute(_menuTrack);
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e) => ViewModel.RemoveFromLibraryCommand.Execute(_menuTrack);

    private void OnDeleteClick(object sender, RoutedEventArgs e) => ViewModel.DeleteFilesCommand.Execute(_menuTrack);

    private void OnFavoriteButtonClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrackViewModel track) ViewModel.ToggleFavoriteCommand.Execute(track);
    }

    private void OnRemoveSelectionClick(object sender, RoutedEventArgs e) => ViewModel.RemoveFromLibraryCommand.Execute(null);

    private void OnDeleteSelectionClick(object sender, RoutedEventArgs e) => ViewModel.DeleteFilesCommand.Execute(null);

    private void OnClearSelectionClick(object sender, RoutedEventArgs e) => TrackList.SelectedItems.Clear();

    private void OnAlbumClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumViewModel album)
        {
            ViewModel.OpenAlbumCommand.Execute(album);
            SelectView("Songs");
        }
    }

    private void OnArtistClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ArtistViewModel artist)
        {
            ViewModel.OpenArtistCommand.Execute(artist);
            SelectView("Songs");
        }
    }

    private void SelectView(string tag)
    {
        var item = ViewSelector.Items.FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null) ViewSelector.SelectedItem = item;
    }

    private void OnDuplicatePlayClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrackViewModel track) ViewModel.PlayCommand.Execute(track);
    }

    private void OnDuplicateRevealClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrackViewModel track) ViewModel.OpenLocationCommand.Execute(track);
    }

    private async void OnDuplicateDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrackViewModel track) return;
        TrackList.SelectedItems.Clear();
        await ViewModel.DeleteFilesCommand.ExecuteAsync(track);
        ViewModel.ShowViewCommand.Execute("Duplicates");
    }
}
