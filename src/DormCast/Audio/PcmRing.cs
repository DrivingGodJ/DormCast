namespace DormCast.Audio;

// Interleaved float stereo. Old samples are discarded instead of building latency.
public sealed class PcmRing(int capacity)
{
    readonly float[] _buffer = new float[capacity];
    readonly object _gate = new();
    int _read, _count;
    long _dropped;
    public long DroppedSamples { get { lock (_gate) return _dropped; } }
    public int Count { get { lock (_gate) return _count; } }
    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            if (samples.Length >= _buffer.Length)
            {
                _dropped += _count + samples.Length - _buffer.Length;
                samples = samples[^_buffer.Length..]; _read = 0; _count = 0;
            }
            var remove = Math.Max(0, _count + samples.Length - _buffer.Length);
            _read = (_read + remove) % _buffer.Length; _count -= remove; _dropped += remove;
            var write = (_read + _count) % _buffer.Length;
            var first = Math.Min(samples.Length, _buffer.Length - write);
            samples[..first].CopyTo(_buffer.AsSpan(write)); samples[first..].CopyTo(_buffer);
            _count += samples.Length;
        }
    }
    public int Read(Span<float> target)
    {
        lock (_gate)
        {
            var n = Math.Min(target.Length, _count);
            var first = Math.Min(n, _buffer.Length - _read);
            _buffer.AsSpan(_read, first).CopyTo(target); _buffer.AsSpan(0, n - first).CopyTo(target[first..]);
            target[n..].Clear(); _read = (_read + n) % _buffer.Length; _count -= n; return n;
        }
    }
    public void Clear(bool countDropped = false)
    {
        lock (_gate) { if (countDropped) _dropped += _count; _read = 0; _count = 0; }
    }
}
