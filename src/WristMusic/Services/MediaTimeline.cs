namespace WristMusic.Services;

internal sealed record MediaTimeline(double Position, double Min, double Max, bool Known, DateTimeOffset Updated, double Rate)
{
    internal static MediaTimeline Normalize(double position, double start, double end, double seekMin, double seekMax,
        DateTimeOffset updated, double? rate, DateTimeOffset now)
    {
        var min = double.IsFinite(start) ? Math.Max(0, start) : 0;
        var max = double.IsFinite(end) && end > min ? end : double.IsFinite(seekMax) && seekMax > min ? seekMax : 0;
        var validStamp = updated.Year > 2000 && updated <= now && now - updated < TimeSpan.FromDays(1);
        var known = max > min || (double.IsFinite(position) && position > 0) || validStamp;
        var safePosition = double.IsFinite(position) ? Math.Max(min, position) : min;
        if (max > min) safePosition = Math.Min(safePosition, max);
        return new(safePosition, min, max, known, validStamp ? updated : now,
            validStamp && double.IsFinite(rate ?? 1) ? Math.Clamp(rate ?? 1, 0, 16) : 0);
    }
    internal double At(DateTimeOffset now, bool playing)
    {
        if (!Known) return 0;
        var value = Position + (playing ? Math.Max(0, (now - Updated).TotalSeconds) * Rate : 0);
        return Max > Min ? Math.Clamp(value, Min, Max) : Math.Max(Min, value);
    }
}
