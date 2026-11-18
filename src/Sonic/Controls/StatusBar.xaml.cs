using Microsoft.UI.Xaml.Controls;
using Sonic.ViewModels;

namespace Sonic.Controls;

/// <summary>Compact footer showing tool status, active downloads and the app version.</summary>
public sealed partial class StatusBar : UserControl
{
    public StatusBar()
    {
        ViewModel = App.GetService<ShellViewModel>();
        InitializeComponent();
    }

    public ShellViewModel ViewModel { get; }
}
