using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sonic.Core.Models;
using Sonic.ViewModels;

namespace Sonic.Controls;

/// <summary>Now-playing bar shown above the status bar while something is queued.</summary>
public sealed partial class PlayerBar : UserControl
{
    public PlayerBar()
    {
        Player = App.GetService<PlayerViewModel>();
        InitializeComponent();
    }

    public PlayerViewModel Player { get; }

    public Brush RepeatBrush(bool on) =>
        (Brush)Application.Current.Resources[on ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush"];

    private void OnQueueItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track) Player.PlayQueueItemCommand.Execute(track);
    }
}
