using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WristMusic.Services;

namespace WristMusic;

internal static class CursorOverlayTests
{
    internal static void Run(string folder, Action<bool, string> check)
    {
        var light = CursorSprite.CreatePixels(false); var dark = CursorSprite.CreatePixels(true);
        var size = CursorSprite.PixelSize; var center = (size / 2 * size + size / 2) * 4;
        var ring = (size / 2 * size + size / 2 + 6) * 4;
        check(light[3] == 0 && light[center + 3] > 240 && light[ring + 2] > light[ring],
            "Cursor sprite has transparent edges, an opaque center and a blue ring");
        check(!light.AsSpan().SequenceEqual(dark), "Cursor sprite follows both existing theme palettes");
        var background = Background(820, 405); var original = background.ToArray();
        var frame = new CursorFrameComposer(); frame.SetPanel(background, 820, 405);
        var first = frame.SetCursor(new(410, 200), false);
        check(first.Width <= 65 && first.Height <= 65 && !frame.Pixels.AsSpan().SequenceEqual(original) && background.AsSpan().SequenceEqual(original),
            "The displayed panel frame itself contains the cursor while its source background stays unchanged");
        var centerIndex = (200 * 820 + 410) * 4;
        check(frame.Pixels[centerIndex] > 240 && frame.Pixels[centerIndex + 3] == 255,
            "The cursor's opaque center is present at the exact controller hit pixel in the final panel image");
        using var texture = new OverlayGpuTexture(software: true);
        texture.Upload(frame.Pixels, 820, 405); var handle = texture.Handle;
        check(texture.ReadBack().AsSpan().SequenceEqual(frame.Pixels), "The same GPU texture used for the panel contains the visible composited cursor");
        var matches = true;
        for (var i = 0; i < 180; i++)
        {
            var dirty = frame.SetCursor(new(50.25 + i * 3.75, 180.5), false);
            texture.UploadRegion(frame.Pixels, 820, 405, dirty);
            if (i % 30 == 0) matches &= texture.ReadBack().AsSpan().SequenceEqual(frame.Pixels);
        }
        check(matches && texture.Handle == handle && texture.Generation == 1 && texture.ReadBack().AsSpan().SequenceEqual(frame.Pixels),
            "180 fractional cursor movements update only dirty regions of one persistent panel texture with exact GPU readback");
        var lightFrame = frame.Pixels.ToArray();
        var themeArea = frame.SetCursor(new(721.5, 180.5), true); texture.UploadRegion(frame.Pixels, 820, 405, themeArea);
        check(!lightFrame.AsSpan().SequenceEqual(frame.Pixels) && texture.ReadBack().AsSpan().SequenceEqual(frame.Pixels),
            "Switching theme repaints the cursor inside the existing panel texture");
        Save(frame.Pixels, 820, 405, Path.Combine(folder, "cursor-panel-dark.png"));
        var clear = frame.SetCursor(null, true); texture.UploadRegion(frame.Pixels, 820, 405, clear);
        check(frame.Pixels.AsSpan().SequenceEqual(original) && texture.ReadBack().AsSpan().SequenceEqual(original),
            "Losing the ray hit restores all old cursor pixels without a ghost trail");
        check(frame.SetCursor(null, true).Width == 0, "A repeatedly hidden cursor requires no further texture update");
        var previewRect = new Int32Rect(440, 130, CameraPreview.Width, CameraPreview.Height);
        var cameraPixels = new byte[CameraPreview.Size];
        Array.Fill(cameraPixels, (byte)25);
        frame.SetCursor(new(550, 200), true);
        frame.SetPreview(cameraPixels, previewRect);
        texture.UploadRegion(frame.Pixels, 820, 405, previewRect);
        var beneath = (200 * 820 + 550) * 4;
        check(frame.Pixels[beneath] != 25 && texture.ReadBack().AsSpan().SequenceEqual(frame.Pixels),
            "A preview upload preserves the cursor in the same VR texture");
        frame.SetCursor(null, true);
        check(frame.Pixels[beneath] == 25 && frame.Pixels[beneath + 3] == 255,
            "Moving the cursor off the preview restores the latest opaque video pixels");
        frame.SetPanel(original, 820, 405);
        frame.SetCursor(new(410, 200), false); var integerFrame = frame.Pixels.ToArray();
        frame.SetCursor(new(410.25, 200), false);
        check(!frame.Pixels.AsSpan().SequenceEqual(integerFrame), "Sub-pixel sampling responds to movement smaller than one panel pixel");
        frame.SetCursor(new(double.NaN, 50), false);
        check(frame.Pixels.AsSpan().SequenceEqual(original), "Invalid coordinates clear the cursor safely");
        frame.SetCursor(new(410, 450), false);
        check(frame.Pixels.AsSpan().SequenceEqual(original), "Coordinates outside the compact panel cannot leave a stray cursor");
        frame.SetPanel(Background(820, 500), 820, 500); frame.SetCursor(new(410, 450), false);
        check(frame.Pixels[(450 * 820 + 410) * 4] > 240, "The cursor is visible in the expanded VR settings area");
        frame.SetPanel(background, 820, 405);
        check(frame.Pixels.AsSpan().SequenceEqual(original), "Shrinking back to compact mode clears a cursor outside the new bounds");
        foreach (var point in new[] { new Point(0, 0), new Point(820, 405), new Point(.5, 404.75) })
        {
            var dirty = frame.SetCursor(point, false);
            texture.UploadRegion(frame.Pixels, 820, 405, dirty);
        }
        check(texture.ReadBack().AsSpan().SequenceEqual(frame.Pixels), "Clipped cursor updates at every panel edge remain inside the GPU texture");
        frame.SetCursor(new(410, 200), false);
        Save(frame.Pixels, 820, 405, Path.Combine(folder, "cursor-panel-light.png"));
        Save(light, size, size, Path.Combine(folder, "cursor-light.png")); Save(dark, size, size, Path.Combine(folder, "cursor-dark.png"));
        using var vm = new MainViewModel(true, new SettingsStore(Path.Combine(folder, "cursor-ui-settings.json")));
        var view = new Views.OverlayView { DataContext = vm };
        var realPanelVerified = true;
        foreach (var darkMode in new[] { false, true })
        {
            vm.DarkMode = darkMode;
            view.Measure(new(820, 500)); view.Arrange(new(0, 0, 820, 500)); view.UpdateLayout();
            var visual = new RenderTargetBitmap(820, 500, 96, 96, PixelFormats.Pbgra32); visual.Render(view);
            var converted = new FormatConvertedBitmap(visual, PixelFormats.Bgra32, null, 0);
            var rgba = new byte[820 * 500 * 4]; converted.CopyPixels(rgba, 820 * 4, 0);
            for (var i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
            var button = (FrameworkElement)view.FindName("PlayPauseButton");
            var point = button.TranslatePoint(new(button.ActualWidth / 2, button.ActualHeight / 2), view);
            frame.SetCursor(null, darkMode); frame.SetPanel(rgba, 820, 500); frame.SetCursor(point, darkMode);
            texture.Upload(frame.Pixels, 820, 500);
            realPanelVerified &= !frame.Pixels.AsSpan().SequenceEqual(rgba) && texture.ReadBack().AsSpan().SequenceEqual(frame.Pixels);
            Save(frame.Pixels, 820, 500, Path.Combine(folder, "cursor-music" + (darkMode ? "-dark" : "") + ".png"));
        }
        vm.DarkMode = false;
        check(realPanelVerified, "Actual WPF music panels and their hit-point cursors share the same verified GPU image in both themes");
    }
    private static byte[] Background(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            pixels[i] = (byte)(x % 180); pixels[i + 1] = (byte)(y % 170); pixels[i + 2] = 80;
            pixels[i + 3] = x < 8 && y < 8 ? (byte)0 : (byte)220;
        }
        return pixels;
    }
    private static void Save(byte[] rgba, int width, int height, string path)
    {
        var bgra = rgba.ToArray();
        for (var i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
}
