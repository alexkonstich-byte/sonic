using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Sonic.Core.Models;

namespace Sonic.Converters;

/// <summary>Resolves theme brushes at conversion time so light/dark switches pick up the right color.</summary>
internal static class ThemeBrush
{
    public static Brush Get(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : new SolidColorBrush(Microsoft.UI.Colors.Gray);
}

public sealed partial class ToolStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        ToolState.Ready => ThemeBrush.Get("SystemFillColorSuccessBrush"),
        ToolState.UpdateAvailable => ThemeBrush.Get("SystemFillColorCautionBrush"),
        ToolState.Installing => ThemeBrush.Get("AccentFillColorDefaultBrush"),
        ToolState.Missing or ToolState.Error => ThemeBrush.Get("SystemFillColorCriticalBrush"),
        _ => ThemeBrush.Get("SystemFillColorNeutralBrush"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class DownloadStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        DownloadStatus.Completed => ThemeBrush.Get("SystemFillColorSuccessBrush"),
        DownloadStatus.Failed => ThemeBrush.Get("SystemFillColorCriticalBrush"),
        DownloadStatus.Paused or DownloadStatus.Skipped => ThemeBrush.Get("SystemFillColorCautionBrush"),
        DownloadStatus.Downloading or DownloadStatus.Converting or DownloadStatus.Tagging => ThemeBrush.Get("AccentTextFillColorPrimaryBrush"),
        _ => ThemeBrush.Get("TextFillColorSecondaryBrush"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public bool IsInverted { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language) =>
        (value is true) ^ IsInverted ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        (value is Visibility.Visible) ^ IsInverted;
}
