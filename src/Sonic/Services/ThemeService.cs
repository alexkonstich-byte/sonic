using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Sonic.Core.Models;

namespace Sonic.Services;

public interface IThemeService
{
    ElementTheme ActualTheme { get; }
    void Apply(AppTheme theme);
}

public sealed class ThemeService(IWindowService windows) : IThemeService
{
    public ElementTheme ActualTheme =>
        (windows.MainWindow?.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;

    public void Apply(AppTheme theme)
    {
        var window = windows.MainWindow;
        if (window?.Content is not FrameworkElement root) return;

        root.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        // Keep the caption buttons (minimize/maximize/close) in sync with the app theme.
        window.AppWindow.TitleBar.PreferredTheme = theme switch
        {
            AppTheme.Light => TitleBarTheme.Light,
            AppTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }
}
