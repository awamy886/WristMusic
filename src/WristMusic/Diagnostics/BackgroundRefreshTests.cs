using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Threading;
using WristMusic.Services;

namespace WristMusic;

internal static class BackgroundRefreshTests
{
    internal static async Task Run(string folder, Action<bool, string> check, bool soak = false)
    {
        // Keep a native posted message pending on a private STA, like a desktop
        // queue that has not been drained. Do not inject user input or activate
        // a window. Compare the old Input timer against the actual new scheduler.
        var completion = new TaskCompletionSource<List<(bool Passed, string Name)>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            var results = new List<(bool, string)>();
            var dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                var owner = Environment.CurrentManagedThreadId;
                var currentTicks = 0; var legacyTicks = 0; var correctThread = true;
                using var current = new BackgroundUiTimer(TimeSpan.FromMilliseconds(10), () =>
                { currentTicks++; correctThread &= Environment.CurrentManagedThreadId == owner; }, "Test loop", log: _ => { });
                var legacy = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
                legacy.Tick += (_, _) => legacyTicks++;
                current.Start(); legacy.Start();
                try
                {
                    Pump(250);
                    results.Add((currentTicks > 2 && legacyTicks > 2, "Both old and new timers run with an unobstructed desktop message queue"));
                    if (!PostThreadMessage(GetCurrentThreadId(), Marker, 0, 0)) throw new System.ComponentModel.Win32Exception();
                    var currentBefore = currentTicks; var legacyBefore = legacyTicks;
                    var duration = soak ? 120000 : 650;
                    Pump(duration);
                    results.Add((legacyTicks == legacyBefore, "Reproduced old VR Input timer freeze while a desktop message stays pending"));
                    results.Add((currentTicks - currentBefore >= Math.Max(10, duration / 100) && correctThread,
                        $"VR refresh continues on its owning STA without desktop interaction while the old loop is frozen ({currentTicks - currentBefore} callbacks in {duration}ms)"));
                    DrainMarker(); Pump(200);
                    results.Add((legacyTicks > legacyBefore, "Old loop recovers when the pending desktop message is drained, matching the reported symptom"));
                }
                finally { legacy.Stop(); DrainMarker(); }

                current.Stop(); Pump(50);
                var posted = 0;
                DispatcherHookEventHandler onPost = (_, e) => { if (e.Operation.Priority == DispatcherPriority.Loaded) Interlocked.Increment(ref posted); };
                dispatcher.Hooks.OperationPosted += onPost;
                try
                {
                    current.Start(); current.Start();
                    // Deliberately suspend dispatching, not the pool timer.
                    Thread.Sleep(250);
                    results.Add((Volatile.Read(ref posted) == 1, "A stalled dispatcher queues only one VR update, with no backlog or duplicate Start loop"));
                    var beforeStop = currentTicks;
                    current.Stop(); Pump(100);
                    results.Add((currentTicks == beforeStop, "Stopping cancels a queued callback before it touches disposed resources"));
                    current.Start(); Thread.Sleep(60); current.Stop(); current.Start(); Pump(200);
                    results.Add((currentTicks > beforeStop, "Stop and restart recover cleanly while stale callbacks from the old generation are ignored"));
                    Thread.Sleep(60); current.Dispose(); var beforeDispose = currentTicks; Pump(100);
                    results.Add((currentTicks == beforeDispose, "Disposal prevents queued and future callbacks after shutdown"));
                }
                finally { dispatcher.Hooks.OperationPosted -= onPost; }
                completion.SetResult(results);
            }
            catch (Exception e) { completion.SetException(e); }
            finally { DrainMarker(); dispatcher.InvokeShutdown(); }
        }) { IsBackground = true, Name = "Background refresh regression" };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        foreach (var result in await completion.Task.WaitAsync(TimeSpan.FromSeconds(soak ? 140 : 20))) check(result.Passed, result.Name);

        var store = new SettingsStore(Path.Combine(folder, "background-settings.json")); store.Save(new());
        using var vm = new MainViewModel(false, store);
        var button = new Button { Command = vm.MediaCommand, CommandParameter = "toggle" };
        check(!button.IsEnabled, "Offscreen playback button starts disabled without a media session");
        vm.ApplyMediaState(new("test", "Test", "Test", true, 10, 0, 100, true, true, true, true, null, DateTimeOffset.UtcNow));
        check(button.IsEnabled, "Offscreen command enables immediately when media arrives without mouse, focus or idle requery");
        vm.ApplyMediaState(null);
        check(!button.IsEnabled, "Offscreen command disables immediately when the session disappears");
        vm.UseDemo = true;
        for (var i = 0; i < 5; i++) await vm.RunMedia("toggle");
        check(button.IsEnabled && !vm.Playing, "Repeated VR playback commands re-enable after completion without any desktop interaction");
    }

    private const uint Marker = 0x8123;
    private static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            // Leave only the marker pending; service the dispatcher's real HWND.
            while (PeekMessage(out var message, 0, 0, Marker - 1, 1) || PeekMessage(out message, 0, Marker + 1, uint.MaxValue, 1))
            {
                TranslateMessage(ref message); DispatchMessage(ref message);
                if (clock.ElapsedMilliseconds >= milliseconds) break;
            }
            Thread.Sleep(1);
        }
    }
    private static void DrainMarker() { while (PeekMessage(out _, 0, Marker, Marker, 1)) { } }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
