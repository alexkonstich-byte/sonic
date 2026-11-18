using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Sonic.Controls;

/// <summary>
/// Album art / thumbnail with a placeholder glyph. Accepts a local file path or an http(s) URL and decodes
/// at display size to keep memory low in virtualized lists.
/// </summary>
public sealed partial class ArtworkImage : UserControl
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(string), typeof(ArtworkImage), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty DecodeSizeProperty =
        DependencyProperty.Register(nameof(DecodeSize), typeof(int), typeof(ArtworkImage), new PropertyMetadata(96));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(ArtworkImage), new PropertyMetadata("", (d, e) => ((ArtworkImage)d).Placeholder.Glyph = (string)e.NewValue));

    public static readonly DependencyProperty GlyphSizeProperty =
        DependencyProperty.Register(nameof(GlyphSize), typeof(double), typeof(ArtworkImage), new PropertyMetadata(16d, (d, e) => ((ArtworkImage)d).Placeholder.FontSize = (double)e.NewValue));

    public static readonly DependencyProperty ImageCornerRadiusProperty =
        DependencyProperty.Register(nameof(ImageCornerRadius), typeof(CornerRadius), typeof(ArtworkImage), new PropertyMetadata(new CornerRadius(4), (d, e) => ((ArtworkImage)d).Root.CornerRadius = (CornerRadius)e.NewValue));

    public static readonly DependencyProperty StretchProperty =
        DependencyProperty.Register(nameof(Stretch), typeof(Microsoft.UI.Xaml.Media.Stretch), typeof(ArtworkImage),
            new PropertyMetadata(Microsoft.UI.Xaml.Media.Stretch.UniformToFill, (d, e) => ((ArtworkImage)d).Picture.Stretch = (Microsoft.UI.Xaml.Media.Stretch)e.NewValue));

    private int _version;

    public ArtworkImage()
    {
        InitializeComponent();
        Root.CornerRadius = ImageCornerRadius;
        Placeholder.FontSize = GlyphSize;
    }

    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public int DecodeSize
    {
        get => (int)GetValue(DecodeSizeProperty);
        set => SetValue(DecodeSizeProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    public CornerRadius ImageCornerRadius
    {
        get => (CornerRadius)GetValue(ImageCornerRadiusProperty);
        set => SetValue(ImageCornerRadiusProperty, value);
    }

    public Microsoft.UI.Xaml.Media.Stretch Stretch
    {
        get => (Microsoft.UI.Xaml.Media.Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        _ = ((ArtworkImage)d).LoadAsync(e.NewValue as string);

    private async Task LoadAsync(string? source)
    {
        // Containers are recycled while scrolling; ignore results for a previous source.
        var version = ++_version;
        Picture.Source = null;
        Placeholder.Visibility = Visibility.Visible;
        if (string.IsNullOrWhiteSpace(source)) return;

        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = DecodeSize, DecodePixelType = DecodePixelType.Logical };
            if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                bitmap.UriSource = new Uri(source);
            }
            else
            {
                if (!File.Exists(source)) return;
                var bytes = await File.ReadAllBytesAsync(source);
                if (version != _version) return;
                using var stream = new MemoryStream(bytes).AsRandomAccessStream();
                await bitmap.SetSourceAsync(stream);
            }

            if (version != _version) return;
            Picture.Source = bitmap;
            Placeholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException or System.Runtime.InteropServices.COMException)
        {
            if (version == _version) Placeholder.Visibility = Visibility.Visible;
        }
    }

    private void OnImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Picture.Source = null;
        Placeholder.Visibility = Visibility.Visible;
    }
}
