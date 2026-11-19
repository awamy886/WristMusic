namespace WristMusic.Services;

public sealed class SingleInstanceGate : IDisposable
{
    private readonly Mutex _mutex;
    public bool Entered { get; }
    public SingleInstanceGate(string name = @"Local\XingMengYu.WristMusic.Desktop")
    {
        _mutex = new Mutex(false, name);
        try { Entered = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { Entered = true; }
    }
    public void Dispose() { if (Entered) _mutex.ReleaseMutex(); _mutex.Dispose(); }
}
