using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Sonic.ViewModels;

namespace Sonic.Views;

public sealed partial class DownloadsPage : Page
{
    public DownloadsPage()
    {
        ViewModel = App.GetService<DownloadsViewModel>();
        InitializeComponent();
    }

    public DownloadsViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
    }
}
