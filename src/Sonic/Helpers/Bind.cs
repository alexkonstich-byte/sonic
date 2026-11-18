using Microsoft.UI.Xaml;

namespace Sonic.Helpers;

/// <summary>Small pure functions for x:Bind function bindings.</summary>
public static class Bind
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfText(string? value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfNull(object? value) => value is null ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfNotNull(object? value) => value is null ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfZero(int value) => value == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfPositive(int value) => value > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    public static bool IsTextNotEmpty(string? value) => !string.IsNullOrWhiteSpace(value);

    public static bool IsPositive(int value) => value > 0;

    public static bool And(bool a, bool b) => a && b;

    public static double Opacity(bool dimmed) => dimmed ? 0.5 : 1.0;
}
