using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;
using Sonic.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Sonic.Views;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        ViewModel = App.GetService<HomeViewModel>();
        InitializeComponent();
        ModeSelector.SelectedItem = ViewModel.IsVideoMode ? VideoModeItem : AudioModeItem;
        // Presets can switch the mode from code.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeViewModel.ModeIndex))
            {
                var item = ViewModel.IsVideoMode ? VideoModeItem : AudioModeItem;
                if (ModeSelector.SelectedItem != item) ModeSelector.SelectedItem = item;
            }
        };

        // Ctrl+V anywhere on the page (outside text fields) pastes and analyzes the link.
        var paste = new KeyboardAccelerator { Key = VirtualKey.V, Modifiers = VirtualKeyModifiers.Control };
        paste.Invoked += (_, args) =>
        {
            if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or AutoSuggestBox or RichEditBox) return;
            args.Handled = true;
            ViewModel.PasteCommand.Execute(null);
        };
        KeyboardAccelerators.Add(paste);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    private static T? TagOf<T>(object sender) where T : class => (sender as FrameworkElement)?.Tag as T;

    private void OnPreviewResultClick(object sender, RoutedEventArgs e)
    {
        if (TagOf<SearchResultItem>(sender) is { } item) ViewModel.PreviewResultCommand.Execute(item);
    }

    private void OnResultAudioClick(object sender, RoutedEventArgs e)
    {
        if (TagOf<SearchResultItem>(sender) is { } item) ViewModel.DownloadResultAudioCommand.Execute(item);
    }

    private void OnResultVideoClick(object sender, RoutedEventArgs e)
    {
        if (TagOf<SearchResultItem>(sender) is { } item) ViewModel.DownloadResultVideoCommand.Execute(item);
    }

    private void OnResultOpenClick(object sender, RoutedEventArgs e)
    {
        if (TagOf<SearchResultItem>(sender) is { } item) ViewModel.OpenSearchResultCommand.Execute(item);
    }

    private void OnPreviewItemClick(object sender, RoutedEventArgs e)
    {
        if (TagOf<PlaylistItemChoice>(sender) is { } item) ViewModel.PreviewPlaylistItemCommand.Execute(item);
    }

    private void OnModeSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ViewModel.ModeIndex = sender.SelectedItem == VideoModeItem ? 1 : 0;

    public HomeViewModel ViewModel { get; }

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

    private static readonly string[] ListExtensions = [".txt", ".csv", ".tsv", ".m3u", ".m3u8"];

    private void OnDragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Text) || e.DataView.Contains(StandardDataFormats.WebLink))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Download with Sonic";
        }
    }

    /// <summary>A dropped list file or multi-line text opens the list dialog; a single link goes into the link box.</summary>
    private async void OnDrop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var file = (await e.DataView.GetStorageItemsAsync()).OfType<Windows.Storage.StorageFile>()
                    .FirstOrDefault(f => ListExtensions.Contains(Path.GetExtension(f.Path), StringComparer.OrdinalIgnoreCase));
                if (file is not null) await ViewModel.OpenBatchAsync(file.Path, null);
                return;
            }

            string? text = null;
            if (e.DataView.Contains(StandardDataFormats.WebLink)) text = (await e.DataView.GetWebLinkAsync()).AbsoluteUri;
            else if (e.DataView.Contains(StandardDataFormats.Text)) text = await e.DataView.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text)) return;

            if (text.Trim().Contains('\n')) await ViewModel.OpenBatchAsync(null, text);
            else
            {
                ViewModel.Url = text.Trim();
                if (ViewModel.AnalyzeCommand.CanExecute(null)) await ViewModel.AnalyzeCommand.ExecuteAsync(null);
            }
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
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && ViewModel.CanQuickDownload)
        {
            ViewModel.QuickDownloadCommand.Execute(null);
            return;
        }

        if (ViewModel.AnalyzeCommand.CanExecute(null)) ViewModel.AnalyzeCommand.Execute(null);
    }

    private void OnRecentTrackClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track) ViewModel.PlayTrackCommand.Execute(track);
    }

    private void OnRecentPlaylistClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistSummary playlist) ViewModel.OpenPlaylistCommand.Execute(playlist);
    }
}
