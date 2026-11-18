using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Sonic.ViewModels;

namespace Sonic.Views;

public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        ViewModel = App.GetService<HistoryViewModel>();
        InitializeComponent();
    }

    public HistoryViewModel ViewModel { get; }

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

    private static HistoryItemViewModel? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as HistoryItemViewModel;

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } i) ViewModel.OpenFileCommand.Execute(i);
    }

    private void OnFolderClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } i) ViewModel.OpenFolderCommand.Execute(i);
    }

    private void OnErrorClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } i) ViewModel.ShowErrorCommand.Execute(i);
    }

    private void OnDownloadAgainClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } i) ViewModel.DownloadAgainCommand.Execute(i);
    }

    private void OnCopyLinkClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } i) ViewModel.CopyLinkCommand.Execute(i);
    }

    private void OnSourceClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } i) ViewModel.OpenSourceCommand.Execute(i);
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } i) ViewModel.RemoveCommand.Execute(i);
    }
}
