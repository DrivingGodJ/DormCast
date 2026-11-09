namespace DormCast.Synchronization;

public sealed class PathLatency
{
    readonly Queue<double> _samples = new();
    double _filtered, _last;
    public int Samples => _samples.Count;
    public double StableMs { get; private set; }
    public void Observe(double value, double nowMs)
    {
        _samples.Enqueue(value); while (_samples.Count > 9) _samples.Dequeue();
        var sorted = _samples.Order().ToArray(); var median = sorted[(sorted.Length - 1) / 2];
        if (_samples.Count <= 3) { StableMs = _filtered = median; _last = nowMs; return; }
        var alpha = 1 - Math.Exp(-Math.Clamp(nowMs - _last, 0, 3000) / 6000); _last = nowMs;
        _filtered += (median - _filtered) * alpha;
        if (_samples.Count >= 3 && Math.Abs(_filtered - StableMs) >= 10) StableMs = Math.Round(_filtered / 5) * 5;
    }
}
