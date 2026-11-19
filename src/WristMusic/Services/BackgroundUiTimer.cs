using System.Diagnostics;
using System.Windows.Threading;

namespace WristMusic.Services;

// VR input is not Win32 input. Input/Background dispatcher work can be deferred
// by pending desktop messages, and WM_TIMER is itself a low-priority message.
// Wake the dispatcher from a pool timer, above that gate, without moving WPF or
// OpenVR resources off their owning thread. Only one callback may be outstanding.
internal sealed class BackgroundUiTimer : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly TimeSpan _interval;
    private readonly Action _tick;
    private readonly string _name;
    private readonly Func<string>? _stage;
    private readonly Action<string> _log;
    private System.Threading.Timer? _timer;
    private Epoch? _active;
    private bool _disposed;

    private sealed class Epoch
    {
        internal int Pending, Running, Warned;
        internal long LastCompleted = Stopwatch.GetTimestamp();
    }

    internal BackgroundUiTimer(TimeSpan interval, Action tick, string name,
        Func<string>? stage = null, Action<string>? log = null)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _interval = interval; _tick = tick; _name = name; _stage = stage; _log = log ?? VrDiagnostics.Write;
    }

    internal void Start()
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active is not null) return;
        var epoch = new Epoch();
        Volatile.Write(ref _active, epoch);
        _timer = new System.Threading.Timer(_ => Wake(epoch), null, _interval, _interval);
    }

    private void Wake(Epoch epoch)
    {
        if (!ReferenceEquals(Volatile.Read(ref _active), epoch)) return;
        if (Interlocked.CompareExchange(ref epoch.Pending, 1, 0) != 0)
        {
            var elapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref epoch.LastCompleted));
            if (elapsed.TotalSeconds >= 3 && Interlocked.CompareExchange(ref epoch.Warned, 1, 0) == 0)
                _log($"{_name} delayed {elapsed.TotalSeconds:F1}s; stage={(Volatile.Read(ref epoch.Running) == 0 ? "dispatcher" : _stage?.Invoke() ?? "callback")}.");
            return;
        }
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                try
                {
                    if (!ReferenceEquals(Volatile.Read(ref _active), epoch)) return;
                    Volatile.Write(ref epoch.Running, 1);
                    _tick();
                    if (Interlocked.Exchange(ref epoch.Warned, 0) != 0) _log(_name + " resumed.");
                }
                finally
                {
                    Interlocked.Exchange(ref epoch.LastCompleted, Stopwatch.GetTimestamp());
                    Volatile.Write(ref epoch.Running, 0);
                    Volatile.Write(ref epoch.Pending, 0);
                }
            }));
        }
        catch (InvalidOperationException) when (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        { Volatile.Write(ref epoch.Pending, 0); }
    }

    internal void Stop()
    {
        _dispatcher.VerifyAccess();
        Volatile.Write(ref _active, null);
        _timer?.Dispose(); _timer = null;
    }
    public void Dispose() { Stop(); _disposed = true; }
}
