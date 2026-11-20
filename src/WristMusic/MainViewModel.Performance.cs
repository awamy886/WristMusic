using WristMusic.Services;

namespace WristMusic;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? _usageLifetime;
    private SystemUsage.Reading _usage;
    public bool ShowCpuUsage
    {
        get => Settings.ShowCpuUsage;
        set { if (ShowCpuUsage != value) { Settings.ShowCpuUsage = value; Changed(); Notify(nameof(UsageLabel)); } }
    }
    public bool ShowMemoryUsage
    {
        get => Settings.ShowMemoryUsage;
        set { if (ShowMemoryUsage != value) { Settings.ShowMemoryUsage = value; Changed(); Notify(nameof(UsageLabel)); } }
    }
    public bool ShowGpuUsage
    {
        get => Settings.ShowGpuUsage;
        set { if (ShowGpuUsage != value) { Settings.ShowGpuUsage = value; Changed(); Notify(nameof(UsageLabel)); } }
    }

    public string UsageLabel
    {
        get
        {
            List<string> items = [];
            if (ShowCpuUsage) items.Add("CPU " + Percent(_usage.Cpu));
            if (ShowMemoryUsage) items.Add("内存 " + Percent(_usage.Memory));
            if (ShowGpuUsage) items.Add("显卡 " + Percent(_usage.Gpu));
            return string.Join("    ", items);
        }
    }
    private static string Percent(double? usage) => usage is double value ? $"{value:0}%" : "—";

    private async Task SampleUsage(CancellationTokenSource lifetime)
    {
        using var sampler = new SystemUsage();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var activeLastTick = false;
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                var cpu = ShowCpuUsage;
                var memory = ShowMemoryUsage;
                var gpu = ShowGpuUsage;
                var active = cpu || memory || gpu;
                if (!active && !activeLastTick) continue;
                activeLastTick = active;
                // Calls are serialized off the UI thread; a final disabled
                // sample releases the GPU query and discards old baselines.
                var sample = await Task.Run(() => sampler.Read(cpu, memory, gpu), lifetime.Token);
                if (_disposed) break;
                if (sample == _usage) continue;
                _usage = sample;
                Notify(nameof(UsageLabel));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { _usageLifetime = null; lifetime.Dispose(); }
    }
}
