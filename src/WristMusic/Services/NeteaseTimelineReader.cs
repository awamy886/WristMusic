using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace WristMusic.Services;

// Read-only accessibility fallback. Never restores, focuses or sends input to the player.
// All provider calls run on one background thread, so a stalled provider cannot freeze WPF.
internal sealed class NeteaseTimelineReader : IDisposable
{
    internal sealed record Sample(string Track, double Position, double Duration, DateTimeOffset ReadAt);
    private readonly object _gate = new();
    private string _track = "";
    private string _identity = "";
    private long _generation;
    private bool _playing;
    private Sample? _sample;
    private bool _disposed;
    private Thread? _worker;
    internal string Detail { get; private set; } = "Waiting";
    public Sample? Read(string track, string? identity = null, bool playing = false)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            identity ??= track;
            if (_identity != identity) { _track = track; _identity = identity; _generation++; _sample = null; }
            _playing = playing;
            if (_worker is null && track.Length > 0)
            {
                _worker = new Thread(Poll) { IsBackground = true, Name = "WristMusic.NetEaseTimeline" };
                _worker.SetApartmentState(ApartmentState.MTA); _worker.Start();
            }
            return _sample is { } s && DateTimeOffset.UtcNow - s.ReadAt < TimeSpan.FromSeconds(3) ? s : null;
        }
    }

    internal static bool TryParse(string text, out double position, out double duration)
    {
        position = duration = 0;
        var match = Regex.Match(text, @"^\s*(\d{1,4}):([0-5]\d)\s*[/|]\s*(\d{1,4}):([0-5]\d)\s*$", RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        position = int.Parse(match.Groups[1].Value) * 60 + int.Parse(match.Groups[2].Value);
        duration = int.Parse(match.Groups[3].Value) * 60 + int.Parse(match.Groups[4].Value);
        return duration > 0 && position <= duration;
    }

    private void Poll()
    {
        IUIAutomationElement? cached = null;
        IUIAutomation automation;
        try
        {
            automation = new CUIAutomation8();
            if (automation is IUIAutomation2 timeouts) { timeouts.ConnectionTimeout = 1000; timeouts.TransactionTimeout = 1000; }
        }
        catch (Exception e) { Detail = e.Message; return; }
        long observed = -1;
        double lastPosition = -1;
        DateTimeOffset lastChange = default, nextScan = default;
        try
        {
        while (true)
        {
            string track; long generation; bool playing;
            lock (_gate) { if (_disposed) return; track = _track; generation = _generation; playing = _playing; }
            if (generation != observed)
            {
                Release(cached); cached = null; observed = generation; lastPosition = -1;
                nextScan = DateTimeOffset.UtcNow.AddSeconds(1);
            }
            Sample? result = null;
            if (track.Length > 0)
            {
                try
                {
                    if (cached is not null && TryParse(cached.CurrentName, out var pos, out var total))
                        result = new(track, pos, total, DateTimeOffset.UtcNow);
                    else { Release(cached); cached = null; }
                    if (cached is null && DateTimeOffset.UtcNow >= nextScan)
                    {
                        nextScan = DateTimeOffset.UtcNow.AddSeconds(2);
                        Detail = "Scanning";
                        cached = Find(automation, track);
                        if (cached is not null && TryParse(cached.CurrentName, out pos, out total))
                            result = new(track, pos, total, DateTimeOffset.UtcNow);
                    }
                }
                catch (Exception e) { Detail = e.Message; Release(cached); cached = null; }
            }
            if (result is not null)
            {
                if (result.Position != lastPosition || !playing) lastChange = DateTimeOffset.UtcNow;
                lastPosition = result.Position;
                // Some NetEase layouts leave an old tooltip accessible after hiding it.
                // Stop publishing that value instead of turning it into a synthetic clock.
                if (playing && DateTimeOffset.UtcNow - lastChange > TimeSpan.FromSeconds(3)) result = null;
            }
            lock (_gate) { if (_generation == generation) _sample = result; }
            Thread.Sleep(500);
        }
        }
        finally { Release(cached); Release(automation); }
    }
    private IUIAutomationElement? Find(IUIAutomation automation, string track)
    {
        var desktop = automation.GetRootElement();
        var processes = Process.GetProcessesByName("cloudmusic");
        try
        {
            foreach (var process in processes)
            {
                var filter = automation.CreatePropertyCondition(30002, process.Id);
                IUIAutomationElementArray? windows = null;
                try
                {
                    windows = desktop.FindAll(TreeScope.TreeScope_Children, filter);
                    for (var w = 0; w < windows.Length; w++)
                    {
                        var root = windows.GetElement(w);
                        IUIAutomationCondition? textFilter = null;
                        IUIAutomationElementArray? texts = null;
                        try
                        {
                            if (!root.CurrentName.StartsWith(track + " -", StringComparison.OrdinalIgnoreCase)) continue;
                            textFilter = automation.CreatePropertyCondition(30003, 50020);
                            texts = root.FindAll(TreeScope.TreeScope_Descendants, textFilter);
                            Detail = "Scanned " + texts.Length + " text elements";
                            for (var t = 0; t < texts.Length; t++)
                            {
                                var element = texts.GetElement(t);
                                var accepted = false;
                                try
                                {
                                    accepted = TryParse(element.CurrentName, out _, out _);
                                    if (accepted) return element;
                                }
                                finally { if (!accepted) Release(element); }
                            }
                        }
                        finally { Release(texts); Release(textFilter); Release(root); }
                    }
                }
                finally { Release(windows); Release(filter); }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); Release(desktop); }
        return null;
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    public void Dispose() { lock (_gate) { _disposed = true; _sample = null; _track = ""; } }
}
