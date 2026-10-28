using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinDock.Views;

internal sealed class TrayIconTint(bool dark) : IValueConverter
{
    static readonly ConditionalWeakTable<BitmapSource, Variants> cache = new();
    sealed record Variants(ImageSource Light, ImageSource Dark);
    public object? Convert(object value, Type type, object parameter, CultureInfo culture)
    {
        if (value is not BitmapSource image) return null;
        var variants = cache.GetValue(image, Make);
        return dark ? variants.Dark : variants.Light;
    }
    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => throw new NotSupportedException();
    static Variants Make(BitmapSource image)
    {
        if (image.Format != PixelFormats.Pbgra32 || image.PixelWidth > 256 || image.PixelHeight > 256) return new(image, image);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        int minimum = 255, maximum = 0, count = 0;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var alpha = pixels[offset + 3];
            if (alpha < 64) continue;
            var r = Math.Min(255, pixels[offset + 2] * 255 / alpha);
            var g = Math.Min(255, pixels[offset + 1] * 255 / alpha);
            var b = Math.Min(255, pixels[offset] * 255 / alpha);
            if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 12) return new(image, image);
            minimum = Math.Min(minimum, Math.Min(r, Math.Min(g, b)));
            maximum = Math.Max(maximum, Math.Max(r, Math.Max(g, b))); count++;
        }
        // Only a uniform neutral silhouette is a template. Leave colored and
        // multitone application artwork exactly as supplied by its owner.
        if (count == 0 || maximum - minimum > 25) return new(image, image);
        return new(minimum >= 220 ? Tint(image, Color.FromRgb(35, 39, 46)) : image,
            maximum <= 40 ? Tint(image, Color.FromRgb(240, 243, 248)) : image);
    }
    static ImageSource Tint(BitmapSource source, Color color)
    {
        var bounds = new Rect(0, 0, source.PixelWidth, source.PixelHeight);
        var drawing = new DrawingGroup { OpacityMask = new ImageBrush(source) { Stretch = Stretch.Fill } };
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(color), null, new RectangleGeometry(bounds)));
        var image = new DrawingImage(drawing); image.Freeze(); return image;
    }
}
