using Microsoft.UI.Xaml.Controls;
using Sonic.ViewModels;

namespace Sonic.Views.Dialogs;

public sealed partial class PlaylistDownloadDialog : ContentDialog
{
    public PlaylistDownloadDialog(PlaylistDownloadViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Closed += (_, _) => ViewModel.Cancel();
    }

    public PlaylistDownloadViewModel ViewModel { get; }

    private async void OnDownloadClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await ViewModel.DownloadAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }
}
