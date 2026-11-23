namespace WristMusic.Services;

public sealed class MemoryOptimizer : Observable, IDisposable
{
    private readonly Func<bool, IProgress<WorkingSetOptimizer.Step>, CancellationToken, Task<WorkingSetOptimizer.Result>> _run;
    private readonly Func<long> _now;
    private readonly Action? _completed;
    private readonly CancellationTokenSource _lifetime = new();
    private long? _lastSample;
    private long _nextRun;
    private bool _running, _deep;
    private SystemUsage.PhysicalMemory? _reading;
    private int _progress;
    private string _status = "", _result = "", _hint = "";

    internal MemoryOptimizer(UserSettings settings, Action save) : this(WorkingSetOptimizer.Run, () => Environment.TickCount64)
    {
        if (settings.LastMemoryOptimization is { } last)
        {
            var remaining = Math.Clamp(30000 - (DateTimeOffset.UtcNow - last).TotalMilliseconds, 0, 30000);
            _nextRun = _now() + (long)Math.Ceiling(remaining);
        }
        _completed = () => { settings.LastMemoryOptimization = DateTimeOffset.UtcNow; save(); };
    }
    internal MemoryOptimizer(Func<bool, IProgress<WorkingSetOptimizer.Step>, CancellationToken, Task<WorkingSetOptimizer.Result>> run, Func<long> now)
    {
        _run = run;
        _now = now;
        SelectMode = new(p => Deep = Equals(p, "deep"), _ => !_running);
        Optimize = new(_ => _ = OptimizeAsync(), _ => !_running);
    }
    public ActionCommand SelectMode { get; }
    public ActionCommand Optimize { get; }
    public bool Deep { get => _deep; private set => Set(ref _deep, value); }
    public int Progress { get => _progress; private set => Set(ref _progress, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Result { get => _result; private set => Set(ref _result, value); }
    public string Hint { get => _hint; private set => Set(ref _hint, value); }
    public string Cooldown => _nextRun > _now() ? $"冷却 {Math.Max(1, (int)Math.Ceiling((_nextRun - _now()) / 1000d))} 秒" : "";
    public string Total => Format(_reading?.Total);
    public string Used => Format(_reading?.Used);
    public string Available => Format(_reading?.Available);
    private static string Format(ulong? bytes) => bytes.HasValue ? $"{bytes.Value / 1073741824d:0.00} G" : "— G";

    internal void Refresh(bool opening = false)
    {
        long now = _now();
        if (!opening && (_lastSample == null || now - _lastSample < 1000)) return;
        _lastSample = now;
        SetReading(SystemUsage.ReadPhysicalMemory());
        Notify(nameof(Cooldown));
        if (now >= _nextRun) Hint = "";
    }
    private void SetReading(SystemUsage.PhysicalMemory? reading)
    {
        _reading = reading;
        Notify(nameof(Total)); Notify(nameof(Used)); Notify(nameof(Available));
    }
    internal async Task OptimizeAsync()
    {
        if (_running || _lifetime.IsCancellationRequested) return;
        if (_now() < _nextRun) { Hint = "已经优化很好啦～"; return; }
        _running = true;
        SelectMode.NotifyCanExecuteChanged(); Optimize.NotifyCanExecuteChanged();
        Progress = 0;
        Status = "正在扫描用户进程…";
        Result = Hint = "";
        var token = _lifetime.Token;
        var progress = new Progress<WorkingSetOptimizer.Step>(step =>
        {
            if (!_running || token.IsCancellationRequested) return;
            Progress = step.Percent; Status = step.Text;
        });
        try
        {
            bool deep = Deep;
            var r = await Task.Run(() => _run(deep, progress, token), token);
            if (token.IsCancellationRequested) return;
            var freed = r.Trimmed > 0 && r.After.Available > r.Before.Available ? r.After.Available - r.Before.Available : 0;
            Result = $"本次释放内存 {Format(freed)}    ·    整理进程 {r.Trimmed} 个\n" +
                $"优化前 {Format(r.Before.Used)}    →    优化后 {Format(r.After.Used)}\n当前可用内存 {Format(r.After.Available)}";
            SetReading(r.After);
            Progress = 100; Status = "优化完成";
            _nextRun = _now() + 30000;
            Notify(nameof(Cooldown));
            _completed?.Invoke();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Status = $"优化未完成 · {ex.Message}"; }
        finally
        {
            _running = false;
            Optimize.NotifyCanExecuteChanged(); SelectMode.NotifyCanExecuteChanged();
        }
    }
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
