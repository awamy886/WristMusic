namespace WristMusic.Services;

internal sealed class DoublePressDetector
{
    private bool _armed, _down;
    private double? _firstPress;
    public bool Update(bool enabled, bool valid, bool down, double now)
    {
        if (!enabled || !valid) { Reset(); return false; }
        if (!_armed) { if (!down) _armed = true; return false; }
        var pressed = down && !_down; _down = down;
        if (!pressed) return false;
        if (_firstPress is double first && now - first is >= .09 and <= .45)
        { _firstPress = null; return true; }
        _firstPress = now; return false;
    }
    public void Reset() { _armed = _down = false; _firstPress = null; }
}
