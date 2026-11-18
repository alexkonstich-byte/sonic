using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Sonic.Core.Models;
using Sonic.ViewModels;

namespace Sonic.Views.Dialogs;

public sealed partial class MetadataEditorDialog : ContentDialog
{
    public MetadataEditorDialog(MetadataEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _ = UpdateArtworkAsync();
    }

    public MetadataEditorViewModel ViewModel { get; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MetadataEditorViewModel.Artwork)) _ = UpdateArtworkAsync();
    }

    private async Task UpdateArtworkAsync()
    {
        if (ViewModel.Artwork is not { Length: > 0 } bytes)
        {
            ArtworkPreview.Source = null;
            return;
        }

        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = 360 };
            using var stream = new MemoryStream(bytes).AsRandomAccessStream();
            await bitmap.SetSourceAsync(stream);
            ArtworkPreview.Source = bitmap;
        }
        catch (Exception)
        {
            // Unsupported or corrupt embedded image: show the placeholder instead.
            ArtworkPreview.Source = null;
        }
    }

    private async void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await ViewModel.SaveAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnLookupItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SpotifyTrack track) ViewModel.ApplyLookupCommand.Execute(track);
    }

    private void OnCoverItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Sonic.Core.Services.CoverCandidate cover) ViewModel.ApplyCoverCommand.Execute(cover);
    }
}
