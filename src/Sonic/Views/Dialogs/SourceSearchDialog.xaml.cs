using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Sonic.ViewModels;
using Windows.System;

namespace Sonic.Views.Dialogs;

public sealed partial class SourceSearchDialog : ContentDialog
{
    public SourceSearchDialog(SourceSearchViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Opened += async (_, _) => await ViewModel.SearchCommand.ExecuteAsync(null);
        Closed += (_, _) => ViewModel.Cancel();
    }

    public SourceSearchViewModel ViewModel { get; }

    private async void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SourceResultViewModel result && await ViewModel.ChooseAsync(result)) Hide();
    }

    private void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        ViewModel.SearchCommand.Execute(null);
    }
}
