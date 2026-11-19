namespace WristMusic.Services;

// Monotonic seconds supplied by the caller; no native VR dependency in the state machine.
internal sealed class OverlayVisibilityState
{
    public bool Visible { get; private set; }
    private bool? _pending;
    private double _pendingAt;
    private double? _trackingLostAt;
    public bool Update(bool enabled, bool tracked, bool desired, double now, bool forceOpen = false)
    {
        if (!enabled) { Reset(); return false; }
        if (!tracked)
        {
            _pending = null; _trackingLostAt ??= now;
            if (now - _trackingLostAt >= .25) Visible = false;
            return Visible;
        }
        _trackingLostAt = null;
        if (forceOpen) { Visible = true; _pending = null; return true; }
        if (desired == Visible) { _pending = null; return Visible; }
        if (_pending != desired) { _pending = desired; _pendingAt = now; }
        if (now - _pendingAt >= (desired ? .12 : .45)) { Visible = desired; _pending = null; }
        return Visible;
    }
    public void Reset() { Visible = false; _pending = null; _trackingLostAt = null; }
}
