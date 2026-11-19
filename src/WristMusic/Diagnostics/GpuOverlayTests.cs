using WristMusic.Services;

namespace WristMusic;

internal static class GpuOverlayTests
{
    internal static void Run(Action<bool, string> check)
    {
        // WARP executes the actual D3D11 upload/copy/readback operations without requiring a VR GPU.
        using var texture = new OverlayGpuTexture(software: true);
        var frame = new byte[820 * 405 * 4];
        for (var i = 0; i < frame.Length; i += 4) { frame[i] = 31; frame[i + 1] = 123; frame[i + 2] = 241; frame[i + 3] = 189; }
        texture.Upload(frame, 820, 405);
        check(texture.ReadBack().AsSpan().SequenceEqual(frame), "Real D3D11 RGBA upload and atomic copy preserve every pixel including alpha");
        var handle = texture.Handle;
        for (var i = 0; i < 120; i++)
        {
            frame[i * 4] = (byte)i;
            texture.Upload(frame, 820, 405);
        }
        check(texture.Handle == handle && texture.Generation == 1 && texture.ReadBack().AsSpan().SequenceEqual(frame),
            "120 GPU frame updates retain the same compositor texture without an empty frame or reallocation");
        var settingsFrame = new byte[820 * 500 * 4]; Array.Fill(settingsFrame, (byte)77);
        texture.Upload(settingsFrame, 820, 500);
        check(texture.Generation == 2 && texture.ReadBack().AsSpan().SequenceEqual(settingsFrame), "Expanding to VR settings creates a complete new GPU surface with correct dimensions");
        texture.Upload(frame, 820, 405);
        check(texture.Generation == 3 && texture.ReadBack().AsSpan().SequenceEqual(frame), "Returning to compact mode uploads the complete smaller GPU surface");
        var rejected = false;
        try { texture.Upload([0], 820, 405); } catch (ArgumentException) { rejected = true; }
        check(rejected && texture.Generation == 3 && texture.ReadBack().AsSpan().SequenceEqual(frame), "An incomplete frame is rejected without replacing the last complete GPU image");
        texture.Dispose(); texture.Dispose();
        check(texture.Handle == 0, "GPU resources can be safely released once or repeatedly on disconnect");
    }
}
