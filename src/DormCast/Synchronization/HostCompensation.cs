namespace DormCast.Synchronization;

// Deliberate sender hold is independent of browser timers; transport backlog is still bounded.
public sealed class HostCompensation
{
    readonly object _gate = new();
    double _desired, _applied, _last;
    bool _initialized;
    public double AppliedMs { get { lock (_gate) return _applied; } }
    public double DesiredMs { get { lock (_gate) return _desired; } }
    public void SetDesired(double milliseconds) { lock (_gate) { _desired = double.IsFinite(milliseconds) ? Math.Clamp(milliseconds, 0, 1960) : 0; if (!_initialized) { _applied = _desired; _initialized = true; } } }
    public double Advance(double nowMs)
    {
        lock (_gate)
        {
            var allowance = Math.Clamp(nowMs - _last, 0, 250) * 0.02;
            _applied += Math.Clamp(_desired - _applied, -allowance, allowance); _last = nowMs; return _applied;
        }
    }
}
