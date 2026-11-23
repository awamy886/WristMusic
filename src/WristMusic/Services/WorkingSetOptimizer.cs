using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WristMusic.Services;

internal static class WorkingSetOptimizer
{
    internal readonly record struct Result(SystemUsage.PhysicalMemory Before, SystemUsage.PhysicalMemory After, int Trimmed);
    internal readonly record struct Step(int Percent, string Text);
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "System Idle Process", "Idle", "Registry", "Memory Compression", "MemCompression",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "audiodg", "dwm"
    };
    internal static bool MeetsThreshold(ulong bytes, bool deep) => deep ? bytes >= 30UL << 20 : bytes > 200UL << 20;

    internal static async Task<Result> Run(bool deep, IProgress<Step> progress, CancellationToken token)
    {
        var before = SystemUsage.ReadPhysicalMemory() ?? throw new IOException("无法读取系统物理内存。");
        using var current = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("无法识别当前用户。");
        int session = current.SessionId;
        var candidates = new List<(int Pid, long Size)>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var p in processes)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (p.Id <= 4 || p.Id == Environment.ProcessId || p.SessionId != session ||
                        Excluded.Contains(p.ProcessName)) continue;
                    long size = p.WorkingSet64;
                    if (size > 0 && MeetsThreshold((ulong)size, deep)) candidates.Add((p.Id, size));
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException) { } // Exited or protected.
            }
        }
        finally { foreach (var p in processes) p.Dispose(); }
        candidates.Sort((a, b) => b.Size.CompareTo(a.Size));
        int trimmed = 0;
        for (int i = 0; i < candidates.Count; ++i)
        {
            token.ThrowIfCancellationRequested();
            var clock = Stopwatch.StartNew();
            try { if (TryTrim(candidates[i].Pid, session, user, deep)) trimmed++; }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or Win32Exception) { }
            progress.Report(new(5 + (i + 1) * 90 / candidates.Count, $"正在整理进程 {i + 1}/{candidates.Count}"));
            // Slow or busy machines get more breathing room after each OS call.
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(350, clock.Elapsed.TotalMilliseconds * 8)), token).ConfigureAwait(false);
        }
        progress.Report(new(96, "正在统计内存变化…"));
        await Task.Delay(1200, token).ConfigureAwait(false);
        var after = SystemUsage.ReadPhysicalMemory() ?? throw new IOException("无法读取优化后的内存数据。");
        return new(before, after, trimmed);
    }

    internal static bool TryTrim(int pid, int session, SecurityIdentifier user, bool deep)
    {
        if (pid <= 4 || pid == Environment.ProcessId) return false;
        // Hold the same handle through identity, threshold and trim checks, even if a PID was reused.
        const uint queryAndSetQuota = 0x1000 | 0x0100;
        using var handle = OpenProcess(queryAndSetQuota, false, pid);
        if (handle.IsInvalid || !ProcessIdToSessionId(pid, out int actual) || actual != session ||
            !IsProcessCritical(handle, out bool critical) || critical) return false;
        var path = new StringBuilder(1024);
        int length = path.Capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref length) ||
            Excluded.Contains(Path.GetFileNameWithoutExtension(path.ToString()))) return false;
        if (!OpenProcessToken(handle, 8, out var token)) return false;
        using (token)
        using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
        {
            if (owner.User != user) return false;
        }
        var counters = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
        return K32GetProcessMemoryInfo(handle, ref counters, counters.Size) &&
            MeetsThreshold(counters.WorkingSetSize, deep) && K32EmptyWorkingSet(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size, PageFaults;
        public nuint PeakWorkingSetSize, WorkingSetSize, PeakPagedPool, PagedPool, PeakNonPagedPool,
            NonPagedPool, PagefileUsage, PeakPagefileUsage;
    }
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(int pid, out int session);
    [DllImport("kernel32.dll")] private static extern bool IsProcessCritical(SafeProcessHandle handle, out bool critical);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags, StringBuilder path, ref int size);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(SafeProcessHandle handle, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll")] private static extern bool K32GetProcessMemoryInfo(SafeProcessHandle handle, ref MemoryCounters counters, uint size);
    [DllImport("kernel32.dll")] private static extern bool K32EmptyWorkingSet(SafeProcessHandle handle);
}
