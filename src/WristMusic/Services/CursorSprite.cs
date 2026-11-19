using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace WristMusic.Services;

internal static class CursorSprite
{
    internal const int PixelSize = 64;
    internal static byte[] CreatePixels(bool dark)
    {
        var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml") };
        var visual = new DrawingVisual { Effect = new DropShadowEffect { Color = (Color)palette["Legacy.BE9BFF.Color"], BlurRadius = 18, ShadowDepth = 0 } };
        using (var drawing = visual.RenderOpen())
            drawing.DrawEllipse((Brush)palette["Legacy.ECDFFF.Brush"], new Pen((Brush)palette["Legacy.9E72FF.Brush"], 3), new(PixelSize / 2d, PixelSize / 2d), 6, 6);
        var bitmap = new RenderTargetBitmap(PixelSize, PixelSize, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var rgba = new byte[PixelSize * PixelSize * 4]; converted.CopyPixels(rgba, PixelSize * 4, 0);
        for (var i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
        return rgba;
    }
}
