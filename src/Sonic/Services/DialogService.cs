using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sonic.Core.Models;
using Sonic.ViewModels;
using Sonic.Views.Dialogs;

namespace Sonic.Services;

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string primaryText, bool destructive = false, string closeText = "Cancel");
    Task ShowMessageAsync(string title, string message);
    Task ShowErrorAsync(string title, string message, string? details = null);
    Task<string?> PromptTextAsync(string title, string? label, string initialText, string primaryText, string? placeholder = null);

    /// <summary>Opens the metadata editor. Returns true when the user saved changes.</summary>
    Task<bool> EditMetadataAsync(int trackId);

    /// <summary>Opens the Spotify import dialog. Returns the imported playlist id, if any.</summary>
    Task<int?> ImportSpotifyAsync(string? initialUrl);

    /// <summary>Searches YouTube for a source matching the entry and lets the user pick one to download.</summary>
    Task<bool> FindSourceAsync(PlaylistEntry entry);

    /// <summary>Installs or updates a tool, showing progress. Returns true on success.</summary>
    Task<bool> InstallToolAsync(ToolKind kind);

    /// <summary>Opens the batch (list) download dialog, optionally prefilled. Returns the number of queued downloads.</summary>
    Task<int> BatchDownloadAsync(string? filePath = null, string? text = null);

    /// <summary>"Download all" for a playlist; returns how many songs were queued.</summary>
    Task<int> PlaylistDownloadAsync(int playlistId, string playlistName);
}

public sealed class DialogService(IWindowService windows, IServiceProvider services) : IDialogService
{
    // WinUI allows only one open ContentDialog per window.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await _gate.WaitAsync();
        try
        {
            if (windows.XamlRoot is not { } root) return ContentDialogResult.None;
            dialog.XamlRoot = root;
            dialog.Style ??= Application.Current.Resources["DefaultContentDialogStyle"] as Style;
            if (windows.MainWindow?.Content is FrameworkElement fe) dialog.RequestedTheme = fe.ActualTheme;
            return await dialog.ShowAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ConfirmAsync(string title, string message, string primaryText, bool destructive = false, string closeText = "Cancel")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        if (destructive && Application.Current.Resources.TryGetValue("DestructiveButtonStyle", out var style))
            dialog.PrimaryButtonStyle = (Style)style;
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        await ShowAsync(new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        });
    }

    public async Task ShowErrorAsync(string title, string message, string? details = null)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(details))
        {
            panel.Children.Add(new Expander
            {
                Header = "Details",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new ScrollViewer
                {
                    MaxHeight = 220,
                    Content = new TextBlock
                    {
                        Text = details,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                        FontSize = 12,
                    },
                },
            });
        }

        await ShowAsync(new ContentDialog
        {
            Title = title,
            Content = panel,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        });
    }

    public async Task<string?> PromptTextAsync(string title, string? label, string initialText, string primaryText, string? placeholder = null)
    {
        var box = new TextBox { Header = label, Text = initialText, PlaceholderText = placeholder, MinWidth = 320 };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(initialText),
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(box.Text);
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    public async Task<bool> EditMetadataAsync(int trackId)
    {
        var vm = services.GetRequiredService<MetadataEditorViewModel>();
        if (!await vm.LoadAsync(trackId)) return false;
        var dialog = new MetadataEditorDialog(vm);
        await ShowAsync(dialog);
        return vm.Saved;
    }

    public async Task<int?> ImportSpotifyAsync(string? initialUrl)
    {
        var vm = services.GetRequiredService<SpotifyImportViewModel>();
        vm.Url = initialUrl ?? string.Empty;
        var dialog = new SpotifyImportDialog(vm);
        await ShowAsync(dialog);
        return vm.ImportedPlaylistId;
    }

    public async Task<bool> FindSourceAsync(PlaylistEntry entry)
    {
        var vm = services.GetRequiredService<SourceSearchViewModel>();
        vm.Initialize(entry);
        var dialog = new SourceSearchDialog(vm);
        await ShowAsync(dialog);
        return vm.Queued;
    }

    public async Task<int> BatchDownloadAsync(string? filePath = null, string? text = null)
    {
        var vm = services.GetRequiredService<BatchDownloadViewModel>();
        if (filePath is not null) await vm.LoadFileAsync(filePath);
        else if (!string.IsNullOrWhiteSpace(text)) vm.LoadText(text);
        await ShowAsync(new BatchDownloadDialog(vm));
        return vm.QueuedCount;
    }

    public async Task<int> PlaylistDownloadAsync(int playlistId, string playlistName)
    {
        var vm = services.GetRequiredService<PlaylistDownloadViewModel>();
        vm.Initialize(playlistId, playlistName);
        await ShowAsync(new PlaylistDownloadDialog(vm));
        return vm.QueuedCount;
    }

    public async Task<bool> InstallToolAsync(ToolKind kind)
    {
        var vm = services.GetRequiredService<ToolInstallViewModel>();
        vm.Initialize(kind);
        var dialog = new ToolInstallDialog(vm);
        await ShowAsync(dialog);
        return vm.Succeeded;
    }
}
