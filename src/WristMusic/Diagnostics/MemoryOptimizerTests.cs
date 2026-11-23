using WristMusic.Services;

namespace WristMusic;

internal static class MemoryOptimizerTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        const ulong mb = 1UL << 20, gb = 1UL << 30;
        check(!WorkingSetOptimizer.MeetsThreshold(200 * mb, false) && WorkingSetOptimizer.MeetsThreshold(200 * mb + 1, false) &&
            !WorkingSetOptimizer.MeetsThreshold(30 * mb - 1, true) && WorkingSetOptimizer.MeetsThreshold(30 * mb, true),
            "Standard and deep working-set thresholds use their exact byte boundaries");
        long now = 100;
        int calls = 0;
        bool selected = false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tool = new MemoryOptimizer(async (deep, progress, token) =>
        {
            Interlocked.Increment(ref calls);
            selected = deep;
            progress.Report(new(50, "正在整理"));
            await release.Task.WaitAsync(token);
            return new(new(16 * gb, 4 * gb, 75), new(16 * gb, 5 * gb, 69), 2);
        }, () => now);
        tool.SelectMode.Execute("deep");
        var first = tool.OptimizeAsync();
        await tool.OptimizeAsync();
        check(!tool.SelectMode.CanExecute(null) && !tool.Optimize.CanExecute(null), "The two toolbox surfaces cannot start overlapping runs");
        release.SetResult();
        await first;
        check(calls == 1 && selected && tool.Status == "优化完成" && tool.Progress == 100 &&
              tool.Result.Contains("整理进程 2 个") && tool.Result.Contains("本次释放内存 1.00 G") &&
              tool.Used == "11.00 G" && tool.Total == "16.00 G" && tool.Available == "5.00 G",
            "The inline report uses measured physical memory and counts successful trims");
        now += 29999;
        tool.SelectMode.Execute("standard"); await tool.OptimizeAsync();
        check(calls == 1 && tool.Hint == "已经优化很好啦～" && tool.Result.Length > 0,
            "Changing the mode during cooldown preserves results and cannot bypass cooldown");
        now++; await tool.OptimizeAsync();
        check(calls == 2 && !selected && tool.Hint == "", "Optimization is available again exactly 30 seconds after completion");
        using var restarted = new MemoryOptimizer(new UserSettings { LastMemoryOptimization = DateTimeOffset.UtcNow }, () => { });
        check(restarted.Cooldown.Length > 0, "Restarting the application preserves the remaining cooldown");
    }
}
