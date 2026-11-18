using Microsoft.UI.Xaml.Controls;
using Sonic.ViewModels;

namespace Sonic.Views.Dialogs;

/// <summary>Runs an install/update as soon as it opens and turns Cancel into Close when finished.</summary>
public sealed partial class ToolInstallDialog : ContentDialog
{
    public ToolInstallDialog(ToolInstallViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Opened += async (_, _) =>
        {
            await ViewModel.RunAsync();
            CloseButtonText = "Close";
        };
    }

    public ToolInstallViewModel ViewModel { get; }

    private void OnCloseClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (ViewModel.IsRunning) ViewModel.Cancel();
    }
}
