using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WristMusic.Services;

public sealed class CameraPreview : Observable, IDisposable
{
    internal const int Width = 320, Height = 180, Size = Width * Height * 4;
    private readonly HashSet<object> _viewers = [];
    private readonly object _gate = new();
    private readonly BackgroundUiTimer _timer;
    private CancellationTokenSource? _session;
    private byte[]? _pending;
    private string _pendingStatus = "";
    private bool _changed, _disposed;
    private WriteableBitmap? _bitmap;
    private long _lastFrame;
    private string _status = "等待相机串流";
    internal byte[]? Pixels { get; private set; }
    internal long Revision { get; private set; }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public BitmapSource? Image => Pixels == null ? null : _bitmap;

    public CameraPreview() => _timer = new(TimeSpan.FromMilliseconds(16), Present, "Camera preview");

    internal void Watch(object viewer, bool visible)
    {
        if (_disposed) return;
        if (visible)
        {
            if (!_viewers.Add(viewer) || _viewers.Count != 1) return;
            _session = new();
            var session = _session;
            _ = Task.Run(() => Receive(session));
            _timer.Start();
        }
        else if (_viewers.Remove(viewer) && _viewers.Count == 0) Stop();
    }

    private async Task Receive(CancellationTokenSource session)
    {
        var token = session.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try { await ReadWorker(token); }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    VrDiagnostics.Write("Camera preview: " + ex.Message);
                    Offer(null, "预览暂不可用，正在重连", token);
                }
                await Task.Delay(1500, token);
            }
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        finally { session.Dispose(); }
    }

    private async Task ReadWorker(CancellationToken token)
    {
        var name = "WristMusic.Camera." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, Size + 1, 0);
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "WristCamera.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        info.ArgumentList.Add("--preview"); info.ArgumentList.Add(name);
        using var child = Process.Start(info) ?? throw new IOException("无法启动预览组件");
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(6));
            await pipe.WaitForConnectionAsync(startup.Token);
            var header = new byte[1];
            while (true)
            {
                await pipe.ReadExactlyAsync(header, token);
                if (header[0] == 0) { Offer(null, "等待相机串流", token); continue; }
                if (header[0] != 1) throw new InvalidDataException("预览帧格式错误");
                var frame = ArrayPool<byte>.Shared.Rent(Size);
                try { await pipe.ReadExactlyAsync(frame.AsMemory(0, Size), token); }
                catch { ArrayPool<byte>.Shared.Return(frame); throw; }
                Offer(frame, "", token);
            }
        }
        finally
        {
            pipe.Dispose();
            child.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!child.HasExited) child.Kill(); }
        }
    }

    // Single-slot mailbox. The pipe reader never queues a dispatcher callback per frame.
    private void Offer(byte[]? frame, string status, CancellationToken token)
    {
        lock (_gate)
        {
            if (!token.IsCancellationRequested)
            {
                (frame, _pending) = (_pending, frame);
                _pendingStatus = status;
                _changed = true;
            }
        }
        Return(frame);
    }
    private static void Return(byte[]? frame) { if (frame != null) ArrayPool<byte>.Shared.Return(frame); }

    private void Present()
    {
        byte[]? frame;
        string status;
        lock (_gate)
        {
            if (!_changed)
            {
                if (Pixels == null || Stopwatch.GetElapsedTime(_lastFrame).TotalSeconds < 2) return;
                status = "等待相机画面";
                frame = null;
            }
            else
            {
                frame = _pending; _pending = null;
                status = _pendingStatus; _changed = false;
            }
        }
        if (frame == null) { ClearImage(); Status = status; return; }
        _bitmap ??= new(Width, Height, 96, 96, PixelFormats.Bgr32, null);
        if (!_bitmap.TryLock(new Duration(TimeSpan.Zero))) { Return(frame); return; }
        try
        {
            Marshal.Copy(frame, 0, _bitmap.BackBuffer, Size);
            _bitmap.AddDirtyRect(new(0, 0, Width, Height));
        }
        finally { _bitmap.Unlock(); }
        var old = Pixels;
        Pixels = frame; Revision++;
        _lastFrame = Stopwatch.GetTimestamp();
        if (old == null) Notify(nameof(Image));
        Return(old); Status = "";
    }
    private void ClearImage()
    {
        if (Pixels == null) return;
        var old = Pixels;
        Pixels = null; Revision++;
        Notify(nameof(Image)); Return(old);
    }
    private void Stop()
    {
        _timer.Stop();
        _session?.Cancel(); _session = null;
        lock (_gate) { Return(_pending); _pending = null; _changed = false; }
        ClearImage(); Status = "等待相机串流";
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _viewers.Clear(); Stop(); _timer.Dispose();
    }
}
