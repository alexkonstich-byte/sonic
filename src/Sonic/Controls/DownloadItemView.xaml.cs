using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sonic.ViewModels;

namespace Sonic.Controls;

/// <summary>A download row used on the Home and Downloads pages.</summary>
public sealed partial class DownloadItemView : UserControl
{
    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(DownloadItemViewModel), typeof(DownloadItemView),
            new PropertyMetadata(null, (d, _) => ((DownloadItemView)d).Bindings.Update()));

    public DownloadItemView()
    {
        InitializeComponent();
    }

    public DownloadItemViewModel Item
    {
        get => (DownloadItemViewModel)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }
}
