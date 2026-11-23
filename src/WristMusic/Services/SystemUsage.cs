using System.Runtime.InteropServices;

namespace WristMusic.Services;

// Used by a single background sampler. Null means the counter is unavailable
// or needs a second sample, and is deliberately distinct from zero utilization.
internal sealed class SystemUsage : IDisposable
{
    internal readonly record struct Reading(double? Cpu, double? Memory, double? Gpu);
    private (ulong Idle, ulong Total)? _previous;
    private nint _query, _counter;
    private long _retryAt;

    internal Reading Read(bool cpu, bool memory, bool gpu)
    {
        if (!cpu) _previous = null;
        if (!gpu) CloseGpu();
        return new(cpu ? ReadCpu() : null, memory ? ReadPhysicalMemory()?.Load : null, gpu ? ReadGpu() : null);
    }

    private double? ReadCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) { _previous = null; return null; }
        var total = kernel + user; // Kernel time includes idle time.
        var previous = _previous;
        _previous = (idle, total);
        if (previous is not { } p || total <= p.Total || idle < p.Idle) return null;
        return Math.Clamp(100.0 * (1 - (double)(idle - p.Idle) / (total - p.Total)), 0, 100);
    }

    internal readonly record struct PhysicalMemory(ulong Total, ulong Available, uint Load)
    {
        public ulong Used => Total - Available;
    }
    internal static PhysicalMemory? ReadPhysicalMemory()
    {
        var state = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref state) && state.TotalPhysical > 0 && state.AvailablePhysical <= state.TotalPhysical
            ? new(state.TotalPhysical, state.AvailablePhysical, state.Load) : null;
    }

    private double? ReadGpu()
    {
        if (_query == 0)
        {
            if (Environment.TickCount64 < _retryAt) return null;
            _retryAt = Environment.TickCount64 + 30000;
            if (PdhOpenQueryW(null, 0, out _query) != 0) return null;
            if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", 0, out _counter) != 0)
            {
                CloseGpu(); return null;
            }
            _retryAt = 0;
        }
        if (PdhCollectQueryData(_query) != 0)
        {
            CloseGpu(); _retryAt = Environment.TickCount64 + 30000; return null;
        }
        const uint formatDouble = 0x200, moreData = 0x800007d2;
        uint bytes = 0;
        if (PdhGetFormattedCounterArrayW(_counter, formatDouble, ref bytes, out _, 0) != moreData ||
            bytes == 0 || bytes > 32 * 1024 * 1024) return null;
        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (PdhGetFormattedCounterArrayW(_counter, formatDouble, ref bytes, out var count, buffer) != 0) return null;
            var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var stride = Marshal.SizeOf<CounterItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * stride);
                if (item.Status > 1 || !double.IsFinite(item.Value) || item.Value < 0) continue;
                var name = Marshal.PtrToStringUni(item.Name);
                if (name is null) continue;
                // Combine processes on one physical engine. Independent engines
                // can work simultaneously, so report the busiest across adapters.
                var start = name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                var end = name.LastIndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
                if (start < 0 || end <= start) continue;
                var engine = name[start..end];
                engines[engine] = engines.GetValueOrDefault(engine) + item.Value;
            }
            return engines.Count == 0 ? null : Math.Clamp(engines.Values.Max(), 0, 100);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private void CloseGpu()
    {
        if (_query != 0) PdhCloseQuery(_query);
        _query = _counter = 0;
    }
    public void Dispose() => CloseGpu();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { public nint Name; public uint Status; public double Value; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus state);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint PdhOpenQueryW(string? path, nuint data, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint data, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", ExactSpelling = true)] private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bytes, out uint count, nint items);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
}
