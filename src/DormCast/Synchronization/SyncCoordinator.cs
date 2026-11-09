namespace DormCast.Synchronization;

public sealed record SyncState(double TargetMs, string ReferenceDevice, int MeasuredDevices, double MarginMs, double MaximumMs, double DesiredMs, string Direction, double PlayoutTargetMs);

public sealed class SyncCoordinator
{
    public const double MinimumMs = 250;
    const double MaximumMs = 2000, MarginMs = 60;
    sealed class Measurement(string name)
    {
        public string Name = name;
        public PathLatency Path = new();
        public double UpdatedAt;
        public bool Background;
    }
    readonly object _gate = new();
    readonly Dictionary<string, Measurement> _devices = new();
    double _goal = MinimumMs, _effective = MinimumMs, _hostMs, _lastUpdate;
    double? _pendingSince;
    int _pendingDirection;
    public bool Report(string id, string name, double naturalMs, double uncertaintyMs, bool playing, double nowMs)
    {
        lock (_gate)
        {
            if (!playing) { _devices.Remove(id); return false; }
            if (!double.IsFinite(naturalMs) || !double.IsFinite(uncertaintyMs) || naturalMs is < 0 or > MaximumMs - MarginMs || uncertaintyMs is < 0 or > 50) return false;
            if (!_devices.TryGetValue(id, out var device)) _devices[id] = device = new Measurement(name);
            device.UpdatedAt = nowMs; device.Path.Observe(naturalMs, nowMs); return true;
        }
    }
    public double? Baseline(string id) { lock (_gate) return _devices.TryGetValue(id, out var device) ? device.Path.StableMs : null; }
    public int Samples(string id) { lock (_gate) return _devices.TryGetValue(id, out var device) ? device.Path.Samples : 0; }
    public void Remove(string id) { lock (_gate) _devices.Remove(id); }
    public void SetBackground(string id, bool hidden) { lock (_gate) if (_devices.TryGetValue(id, out var device)) device.Background = hidden; }
    public void SetHostLatency(double value) { lock (_gate) _hostMs = double.IsFinite(value) ? Math.Clamp(value, 0, MaximumMs - MarginMs) : 0; }
    public SyncState GetState(double nowMs)
    {
        lock (_gate)
        {
            foreach (var id in _devices.Where(p => !p.Value.Background && nowMs - p.Value.UpdatedAt > 15000).Select(p => p.Key).ToArray()) _devices.Remove(id);
            var slowest = _hostMs; var reference = _hostMs > 0 ? "主机扬声器" : "等待设备测量";
            foreach (var device in _devices.Values) if (device.Path.Samples >= 3 && device.Path.StableMs > slowest) { slowest = device.Path.StableMs; reference = device.Name; }
            var needed = Math.Clamp(slowest + MarginMs, MinimumMs, MaximumMs);
            var requested = Math.Clamp(Math.Ceiling(needed / 50) * 50, MinimumMs, MaximumMs);
            var pending = requested > _goal && needed > _goal + 20 ? 1 : requested <= _goal - 50 && needed < _goal - 40 ? -1 : 0;
            if (pending != _pendingDirection) { _pendingDirection = pending; _pendingSince = pending == 0 ? null : nowMs; }
            if (pending != 0 && _pendingSince is { } since && nowMs - since >= (pending > 0 ? 8000 : 60000))
            { _goal = requested; _pendingSince = null; _pendingDirection = 0; }
            var allowance = Math.Clamp(nowMs - _lastUpdate, 0, 2000) * 0.02; _lastUpdate = nowMs;
            _effective += Math.Clamp(_goal - _effective, -allowance, allowance);
            var direction = Math.Abs(_effective - _goal) > 0.1 ? _effective < _goal ? "increasing" : "decreasing" : _pendingDirection == 1 ? "observing" : _pendingDirection == -1 ? "settling" : "stable";
            return new SyncState(_goal, reference, _devices.Count, MarginMs, MaximumMs, requested, direction, Math.Round(_effective, 1));
        }
    }
}
