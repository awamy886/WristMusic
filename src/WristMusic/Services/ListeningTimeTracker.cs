using System.Diagnostics;

namespace WristMusic.Services;

internal sealed class ListeningTimeTracker
{
    private readonly Func<double> _clock;
    private double _last;
    private bool _counting;
    public double TotalSeconds { get; private set; }
    public ListeningTimeTracker(double initialSeconds = 0, Func<double>? clock = null)
    {
        TotalSeconds = Math.Max(0, double.IsFinite(initialSeconds) ? initialSeconds : 0);
        _clock = clock ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        _last = _clock();
    }
    public void Update(bool counting)
    {
        var now = _clock(); var delta = now - _last;
        // Do not count system suspension or a long gap without a fresh playback observation.
        if (_counting && delta is > 0 and <= 5) TotalSeconds = Math.Min(36500d * 86400, TotalSeconds + delta);
        _last = now; _counting = counting;
    }
    public static string Format(double seconds)
    {
        var whole = (long)Math.Max(0, seconds);
        return $"{whole / 3600:00}:{whole / 60 % 60:00}:{whole % 60:00}";
    }
}
