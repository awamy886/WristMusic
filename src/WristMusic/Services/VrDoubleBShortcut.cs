namespace WristMusic.Services;

internal readonly record struct VrButtonState(bool Valid, bool Down);

internal sealed class VrDoubleBShortcut
{
    private readonly DoublePressDetector[] _detectors = [new(), new(), new(), new()];
    private double? _lastOpen;
    internal bool Update(bool enabled, double now, VrButtonState rightAction, VrButtonState leftAction,
        VrButtonState rightPhysical, VrButtonState leftPhysical)
    {
        var open = _detectors[0].Update(enabled, rightAction.Valid, rightAction.Down, now);
        open |= _detectors[1].Update(enabled, leftAction.Valid, leftAction.Down, now);
        open |= _detectors[2].Update(enabled, rightPhysical.Valid, rightPhysical.Down, now);
        open |= _detectors[3].Update(enabled, leftPhysical.Valid, leftPhysical.Down, now);
        if (!enabled) { _lastOpen = null; return false; }
        // The action and physical snapshots can arrive one poll apart for the same press.
        if (!open || _lastOpen is double previous && now - previous < .12) return false;
        _lastOpen = now; return true;
    }
    internal void Reset() { foreach (var detector in _detectors) detector.Reset(); _lastOpen = null; }
}
