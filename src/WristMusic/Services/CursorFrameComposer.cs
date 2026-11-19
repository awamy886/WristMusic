using System.Windows;

namespace WristMusic.Services;

// Cache the WPF background. Cursor updates restore the old small region and blend
// the new sprite into the SAME texture as the panel, without another native overlay.
internal sealed class CursorFrameComposer
{
    private byte[] _background = [], _sprite = [];
    private Point? _point;
    private bool? _dark;
    private Int32Rect _cursorRect;
    internal int Width { get; private set; }
    internal int Height { get; private set; }
    internal byte[] Pixels { get; private set; } = [];
    internal void SetPanel(byte[] rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4)) throw new ArgumentException("Invalid panel frame.");
        if (Width != width || Height != height)
        { _background = new byte[rgba.Length]; Pixels = new byte[rgba.Length]; Width = width; Height = height; }
        rgba.CopyTo(_background, 0); rgba.CopyTo(Pixels, 0);
        _point = Normalize(_point); _cursorRect = Draw();
    }
    internal Int32Rect SetCursor(Point? point, bool dark)
    {
        point = Normalize(point);
        if (_point == point && (point is null || _dark == dark)) return default;
        var previous = _cursorRect;
        Restore(previous); _point = point;
        if (point is not null && (_dark != dark || _sprite.Length == 0))
        { _sprite = CursorSprite.CreatePixels(dark); _dark = dark; }
        _cursorRect = Draw(); return Union(previous, _cursorRect);
    }
    private Point? Normalize(Point? point) => point is { } p && double.IsFinite(p.X) && double.IsFinite(p.Y) &&
        p.X >= 0 && p.X <= Width && p.Y >= 0 && p.Y <= Height && Width > 0 && Height > 0 ? p : null;
    private void Restore(Int32Rect area)
    {
        for (var y = area.Y; y < area.Y + area.Height; y++)
        {
            var offset = (y * Width + area.X) * 4;
            Buffer.BlockCopy(_background, offset, Pixels, offset, area.Width * 4);
        }
    }
    private Int32Rect Draw()
    {
        if (_point is not { } point || _sprite.Length == 0) return default;
        const int size = CursorSprite.PixelSize;
        var left = point.X - size / 2d; var top = point.Y - size / 2d;
        var x0 = Math.Max(0, (int)Math.Floor(left)); var y0 = Math.Max(0, (int)Math.Floor(top));
        var x1 = Math.Min(Width, (int)Math.Ceiling(left + size)); var y1 = Math.Min(Height, (int)Math.Ceiling(top + size));
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            var sx = x - left; var sy = y - top;
            var ix = (int)Math.Floor(sx); var iy = (int)Math.Floor(sy);
            var fx = sx - ix; var fy = sy - iy;
            double a = 0, r = 0, g = 0, b = 0;
            // Interpolate premultiplied samples so sub-pixel movement has no dark fringe.
            void Sample(int px, int py, double weight)
            {
                if ((uint)px >= size || (uint)py >= size || weight <= 0) return;
                var i = (py * size + px) * 4;
                var alpha = _sprite[i + 3] / 255d * weight;
                a += alpha; r += _sprite[i] * alpha; g += _sprite[i + 1] * alpha; b += _sprite[i + 2] * alpha;
            }
            Sample(ix, iy, (1 - fx) * (1 - fy)); Sample(ix + 1, iy, fx * (1 - fy));
            Sample(ix, iy + 1, (1 - fx) * fy); Sample(ix + 1, iy + 1, fx * fy);
            if (a <= 0) continue;
            var target = (y * Width + x) * 4;
            var keep = _background[target + 3] / 255d * (1 - a);
            var alphaOut = a + keep;
            Pixels[target] = Byte((r + _background[target] * keep) / alphaOut);
            Pixels[target + 1] = Byte((g + _background[target + 1] * keep) / alphaOut);
            Pixels[target + 2] = Byte((b + _background[target + 2] * keep) / alphaOut);
            Pixels[target + 3] = Byte(alphaOut * 255);
        }
        return new(x0, y0, x1 - x0, y1 - y0);
    }
    private static byte Byte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    private static Int32Rect Union(Int32Rect a, Int32Rect b)
    {
        if (a.Width == 0 || a.Height == 0) return b;
        if (b.Width == 0 || b.Height == 0) return a;
        var x = Math.Min(a.X, b.X); var y = Math.Min(a.Y, b.Y);
        return new(x, y, Math.Max(a.X + a.Width, b.X + b.Width) - x, Math.Max(a.Y + a.Height, b.Y + b.Height) - y);
    }
}
