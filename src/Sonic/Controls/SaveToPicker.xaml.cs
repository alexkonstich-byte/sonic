using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sonic.Services;

namespace Sonic.Controls;

/// <summary>"Save to" folder field: type a path, pick a recent folder, browse, or reset to the default.</summary>
public sealed partial class SaveToPicker : UserControl
{
    public static readonly DependencyProperty FolderProperty =
        DependencyProperty.Register(nameof(Folder), typeof(string), typeof(SaveToPicker), new PropertyMetadata(string.Empty, OnFolderChanged));

    public static readonly DependencyProperty DefaultFolderProperty =
        DependencyProperty.Register(nameof(DefaultFolder), typeof(string), typeof(SaveToPicker), new PropertyMetadata(string.Empty, (d, _) => ((SaveToPicker)d).UpdateState()));

    public static readonly DependencyProperty RecentFoldersProperty =
        DependencyProperty.Register(nameof(RecentFolders), typeof(IEnumerable<string>), typeof(SaveToPicker), new PropertyMetadata(null, (d, _) => ((SaveToPicker)d).UpdateState()));

    private bool _syncing;

    public SaveToPicker()
    {
        InitializeComponent();
        UpdateState();
    }

    public string Folder
    {
        get => (string)GetValue(FolderProperty);
        set => SetValue(FolderProperty, value);
    }

    public string DefaultFolder
    {
        get => (string)GetValue(DefaultFolderProperty);
        set => SetValue(DefaultFolderProperty, value);
    }

    public IEnumerable<string>? RecentFolders
    {
        get => (IEnumerable<string>?)GetValue(RecentFoldersProperty);
        set => SetValue(RecentFoldersProperty, value);
    }

    private static void OnFolderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (SaveToPicker)d;
        if (!picker._syncing && picker.PathBox.Text != (string?)e.NewValue)
        {
            picker._syncing = true;
            picker.PathBox.Text = e.NewValue as string ?? string.Empty;
            picker._syncing = false;
        }

        picker.UpdateState();
    }

    private IReadOnlyList<string> Recent() =>
        (RecentFolders ?? []).Where(f => !string.Equals(f, DefaultFolder, StringComparison.OrdinalIgnoreCase)).ToList();

    private void UpdateState()
    {
        ResetButton.IsEnabled = !string.Equals(Folder?.Trim().TrimEnd('\\'), DefaultFolder?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        RecentButton.IsEnabled = Recent().Count > 0;
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        Folder = PathBox.Text.Trim().Trim('"');
        _syncing = false;
        UpdateState();
    }

    private void SetFolder(string folder)
    {
        Folder = folder;
        PathBox.Text = folder;
    }

    private void OnRecentOpening(object sender, object e)
    {
        RecentMenu.Items.Clear();
        foreach (var folder in Recent())
        {
            var item = new MenuFlyoutItem { Text = folder, Icon = new FontIcon { Glyph = "" } };
            item.Click += (_, _) => SetFolder(folder);
            RecentMenu.Items.Add(item);
        }
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var folder = await App.GetService<IWindowService>().PickFolderAsync();
        if (folder is not null) SetFolder(folder);
    }

    private void OnResetClick(object sender, RoutedEventArgs e) => SetFolder(DefaultFolder);
}
