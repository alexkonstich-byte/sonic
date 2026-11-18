using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Sonic.ViewModels;
using Windows.System;

namespace Sonic.Views.Dialogs;

public sealed partial class SpotifyImportDialog : ContentDialog
{
    public SpotifyImportDialog(SpotifyImportViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        IsPrimaryButtonEnabled = ViewModel.IsConfigured;
        Opened += async (_, _) =>
        {
            UrlBox.Focus(FocusState.Programmatic);
            if (ViewModel.IsConfigured && !string.IsNullOrWhiteSpace(ViewModel.Url)) await ViewModel.LoadPreviewCommand.ExecuteAsync(null);
        };
        Closed += (_, _) => ViewModel.Cancel();
    }

    public SpotifyImportViewModel ViewModel { get; }

    private async void OnImportClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await ViewModel.ImportAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnUrlKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        if (ViewModel.LoadPreviewCommand.CanExecute(null)) ViewModel.LoadPreviewCommand.Execute(null);
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        Hide();
        ViewModel.OpenSettingsCommand.Execute(null);
    }
}
