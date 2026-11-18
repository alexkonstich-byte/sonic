using Microsoft.UI.Xaml.Controls;
using Sonic.ViewModels;

namespace Sonic.Views.Dialogs;

public sealed partial class BatchDownloadDialog : ContentDialog
{
    public BatchDownloadDialog(BatchDownloadViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ModeSelector.SelectedItem = ViewModel.IsVideoMode ? VideoModeItem : AudioModeItem;
        Closed += (_, _) => ViewModel.Cancel();
    }

    public BatchDownloadViewModel ViewModel { get; }

    private void OnModeSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ViewModel.ModeIndex = sender.SelectedItem == VideoModeItem ? 1 : 0;

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
