using System.Runtime.InteropServices;
using System.Windows;

namespace WristMusic.Services;

// A persistent D3D11 texture, with an atomic GPU copy from a private upload surface.
// The compositor never sees the upload surface being rewritten or a cleared frame.
internal sealed unsafe class OverlayGpuTexture : IDisposable
{
    private nint _device, _context, _upload, _texture;
    private int _width, _height;
    internal nint Handle => _texture;
    internal int Generation { get; private set; }

    internal OverlayGpuTexture(int adapterIndex = -1, bool software = false)
    {
        nint factory = 0, adapter = 0;
        try
        {
            if (adapterIndex >= 0)
            {
                var iid = new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369"); // IDXGIFactory
                Marshal.ThrowExceptionForHR(CreateDXGIFactory(ref iid, out factory));
                var enumerate = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(factory, 7);
                Marshal.ThrowExceptionForHR(enumerate(factory, (uint)adapterIndex, &adapter));
            }
            var result = D3D11CreateDevice(adapter, adapter != 0 ? 0 : software ? 5 : 1, 0, 0x20,
                0, 0, 7, out _device, out _, out _context);
            Marshal.ThrowExceptionForHR(result);
        }
        catch { Dispose(); throw; }
        finally { Release(ref adapter); Release(ref factory); }
    }

    internal void Upload(byte[] rgba, int width, int height)
    {
        if (_device == 0) throw new ObjectDisposedException(nameof(OverlayGpuTexture));
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4)) throw new ArgumentException("Invalid RGBA frame dimensions.");
        if (_width != width || _height != height) Resize(width, height);
        var update = (delegate* unmanaged[Stdcall]<nint, nint, uint, nint, void*, uint, uint, void>)Slot(_context, 48);
        fixed (byte* data = rgba) update(_context, _upload, 0, 0, data, (uint)width * 4, 0);
        Publish();
    }
    internal void UploadRegion(byte[] rgba, int width, int height, Int32Rect area)
    {
        if (_device == 0) throw new ObjectDisposedException(nameof(OverlayGpuTexture));
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4) ||
            area.X < 0 || area.Y < 0 || area.Width <= 0 || area.Height <= 0 ||
            (long)area.X + area.Width > width || (long)area.Y + area.Height > height) throw new ArgumentException("Invalid texture update region.");
        if (_width != width || _height != height) { Upload(rgba, width, height); return; }
        var box = new TextureBox { Left = (uint)area.X, Top = (uint)area.Y, Right = (uint)(area.X + area.Width), Bottom = (uint)(area.Y + area.Height), Back = 1 };
        var update = (delegate* unmanaged[Stdcall]<nint, nint, uint, nint, void*, uint, uint, void>)Slot(_context, 48);
        fixed (byte* data = rgba) update(_context, _upload, 0, (nint)(&box), data + (area.Y * width + area.X) * 4, (uint)width * 4, 0);
        Publish();
    }
    private void Publish()
    {
        var copy = (delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(_context, 47);
        copy(_context, _texture, _upload);
        Flush();
    }

    private void Resize(int width, int height)
    {
        nint upload = 0, texture = 0;
        try
        {
            var description = new TextureDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = 28, SampleCount = 1, BindFlags = 0x28 // RGBA8, shader resource + render target
            };
            upload = CreateTexture(description);
            description.MiscFlags = 2; // D3D11_RESOURCE_MISC_SHARED
            texture = CreateTexture(description);
            // Keep the old surface alive until both replacements have been successfully created.
            Release(ref _upload); Release(ref _texture);
            _upload = upload; _texture = texture; upload = texture = 0;
            _width = width; _height = height; Generation++;
        }
        finally { Release(ref upload); Release(ref texture); }
    }

    internal byte[] ReadBack()
    {
        if (_texture == 0) throw new InvalidOperationException("No frame has been uploaded.");
        var description = new TextureDescription { Width = (uint)_width, Height = (uint)_height,
            MipLevels = 1, ArraySize = 1, Format = 28, SampleCount = 1, Usage = 3, CpuAccessFlags = 0x20000 };
        var staging = CreateTexture(description);
        try
        {
            var copy = (delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(_context, 47);
            copy(_context, staging, _texture);
            var mapped = new MappedResource();
            var map = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedResource*, int>)Slot(_context, 14);
            Marshal.ThrowExceptionForHR(map(_context, staging, 0, 1, 0, &mapped));
            try
            {
                var bytes = new byte[_width * _height * 4];
                for (var y = 0; y < _height; y++) Marshal.Copy(mapped.Data + checked((int)mapped.RowPitch * y), bytes, _width * 4 * y, _width * 4);
                return bytes;
            }
            finally
            {
                var unmap = (delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(_context, 15);
                unmap(_context, staging, 0);
            }
        }
        finally { Release(ref staging); }
    }

    private nint CreateTexture(TextureDescription description)
    {
        nint texture = 0;
        var create = (delegate* unmanaged[Stdcall]<nint, TextureDescription*, nint, nint*, int>)Slot(_device, 5);
        var result = create(_device, &description, 0, &texture);
        if (result < 0) { Release(ref texture); Marshal.ThrowExceptionForHR(result); }
        return texture;
    }
    private void Flush() => ((delegate* unmanaged[Stdcall]<nint, void>)Slot(_context, 111))(_context);
    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];
    private static void Release(ref nint instance)
    {
        if (instance == 0) return;
        ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance); instance = 0;
    }
    public void Dispose()
    {
        Release(ref _upload); Release(ref _texture); Release(ref _context); Release(ref _device);
        _width = _height = 0;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDescription
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MappedResource { public nint Data; public uint RowPitch, DepthPitch; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TextureBox { public uint Left, Top, Front, Right, Bottom, Back; }
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
        nint levels, uint levelCount, uint sdkVersion, out nint device, out uint featureLevel, out nint context);
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory(ref Guid iid, out nint factory);
}
