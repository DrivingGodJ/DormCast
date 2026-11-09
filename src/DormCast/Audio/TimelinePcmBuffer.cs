namespace DormCast.Audio;

// Sample-addressed history: a missed frame yields silence, never an old unbounded queue.
public sealed class TimelinePcmBuffer(int capacityFrames = 48000 * 4)
{
    readonly object _gate = new();
    readonly float[] _samples = new float[capacityFrames * 2];
    readonly long[] _tags = Enumerable.Repeat(long.MinValue, capacityFrames).ToArray();
    public void Write(long startSample, ReadOnlySpan<float> stereo)
    {
        lock (_gate)
        {
            for (var i = 0; i < stereo.Length / 2; i++)
            {
                var at = startSample + i; if (at < 0) continue;
                var slot = (int)(at % capacityFrames);
                _samples[slot * 2] = stereo[i * 2]; _samples[slot * 2 + 1] = stereo[i * 2 + 1]; _tags[slot] = at;
            }
        }
    }
    public double Read(double position, double step, Span<float> target)
    {
        lock (_gate)
        {
            for (var i = 0; i < target.Length / 2; i++, position += step)
            {
                var at = (long)Math.Floor(position); var fraction = (float)(position - at);
                for (var channel = 0; channel < 2; channel++)
                    target[i * 2 + channel] = Sample(at, channel) * (1 - fraction) + Sample(at + 1, channel) * fraction;
            }
            return position;
        }
    }
    float Sample(long at, int channel)
    {
        if (at < 0) return 0;
        var slot = (int)(at % capacityFrames);
        return _tags[slot] == at ? _samples[slot * 2 + channel] : 0;
    }
    public static long Unwrap(uint timestamp, long referenceSample)
        => timestamp + (long)Math.Round((referenceSample - (double)timestamp) / 4294967296d) * 4294967296L;
}
