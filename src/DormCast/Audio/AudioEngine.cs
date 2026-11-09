using System.Diagnostics;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using DormCast.Hosting;

namespace DormCast.Audio;

public sealed record EncodedFrame(byte[] Payload, uint RtpTimestamp, uint Duration, long CapturedAt, byte[]? MonitorPayload = null);

public sealed class AudioEngine(int frameMs, string? captureDeviceId = null) : IDisposable
{
    public const int TimelineStartMs = 40;
    readonly PcmRing _ring = new(48000 * 2 * 60 / 1000);
    readonly CancellationTokenSource _stop = new();
    LoopbackSource? _source;
    Thread? _thread;
    long _frames, _underruns, _skipped, _sequence;
    double _maxEncodeMs;
    float _masterGain = 1;
    public bool EncodeLocalMonitor { get; init; }
    public void SetMasterGain(float gain) => Volatile.Write(ref _masterGain, float.IsFinite(gain) ? Math.Clamp(gain, 0, 1) : 1);
    Exception? _failure;
    public event Action<EncodedFrame>? FrameReady;
    public long EpochTicks { get; private set; }
    public double NowMs => Stopwatch.GetElapsedTime(EpochTicks).TotalMilliseconds;
    public object Snapshot() => new
    {
        device = _source?.DeviceName, capture = _source?.Status, frameMs, sampleRate = 48000, channels = 2, bitrate = 128000, masterGain = Volatile.Read(ref _masterGain),
        capturedSamples = _source?.CapturedSamples ?? 0, peak = _source?.Peak ?? 0,
        actualCaptureBufferMs = _source?.ActualBufferMs ?? 0, captureDiscontinuities = _source?.Discontinuities ?? 0,
        frames = Interlocked.Read(ref _frames), sourceSamples = Interlocked.Read(ref _sequence),
        bufferMs = _ring.Count / 96.0, droppedPcmMs = _ring.DroppedSamples / 96.0,
        underrunFrames = Interlocked.Read(ref _underruns), skippedFrames = Interlocked.Read(ref _skipped), maxEncodeMs = Volatile.Read(ref _maxEncodeMs), error = _failure?.Message
    };
    public string DeviceName => _source?.DeviceName ?? "未启动";
    public string CaptureStatus => _source?.Status ?? "未启动";
    public Exception? Failure => _failure;
    public double Peak => _source?.Peak ?? 0;
    public void Start()
    {
        EpochTicks = Stopwatch.GetTimestamp();
        _source = new LoopbackSource(_ring, captureDeviceId); _source.Start();
        _thread = new Thread(Run) { IsBackground = true, Name = "DormCast Opus timeline", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }
    void Run()
    {
        // Request the finer timer only while broadcasting, and balance it in finally.
        var timerRequested = timeBeginPeriod(1) == 0;
        try
        {
            using var encoder = OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
            using var localEncoder = EncodeLocalMonitor ? OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO) : null;
            encoder.Bitrate = 128000; encoder.Complexity = 5;
            encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
            encoder.UseVBR = true; encoder.UseConstrainedVBR = true;
            encoder.UseDTX = false; encoder.UseInbandFEC = true; encoder.PacketLossPercent = 5;
            if (localEncoder != null) { localEncoder.Bitrate = 128000; localEncoder.Complexity = 5; localEncoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC; localEncoder.UseDTX = false; }
            var frameSize = 48 * frameMs;
            var pcm = new float[frameSize * 2]; var encoded = new byte[1000];
            long sampleIndex = 0;
            var next = EpochTicks + Stopwatch.Frequency * TimelineStartMs / 1000;
            var period = Stopwatch.Frequency * frameMs / 1000;
            while (!_stop.IsCancellationRequested)
            {
                var now = Stopwatch.GetTimestamp();
                if (now < next)
                {
                    _stop.Token.WaitHandle.WaitOne(Math.Max(1, (int)((next - now) * 1000 / Stopwatch.Frequency)));
                    continue;
                }
                var missed = (now - next) / period;
                if (missed > 0)
                {
                    sampleIndex += missed * frameSize; next += missed * period;
                    Interlocked.Add(ref _skipped, missed); _ring.Clear(true);
                }
                if (_source!.LastPacket != 0 && Stopwatch.GetElapsedTime(_source.LastPacket).TotalMilliseconds > 80) _ring.Clear(true);
                if (_ring.Read(pcm) < pcm.Length) Interlocked.Increment(ref _underruns);
                var begin = Stopwatch.GetTimestamp();
                // The physical endpoint already applies Windows volume. Keep a separate, unscaled
                // local packet instead of dividing decoded lossy audio by a near-zero gain.
                byte[]? monitorPayload = null;
                if (localEncoder != null) { var localCount = localEncoder.Encode(pcm, frameSize, encoded, encoded.Length); monitorPayload = encoded.AsSpan(0, localCount).ToArray(); }
                var masterGain = Volatile.Read(ref _masterGain);
                for (var i = 0; i < pcm.Length; i++) pcm[i] *= masterGain;
                var count = encoder.Encode(pcm, frameSize, encoded, encoded.Length);
                var elapsed = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
                if (elapsed > _maxEncodeMs) Volatile.Write(ref _maxEncodeMs, elapsed);
                FrameReady?.Invoke(new EncodedFrame(encoded.AsSpan(0, count).ToArray(), unchecked((uint)sampleIndex), (uint)frameSize, now, monitorPayload));
                sampleIndex += frameSize; next += period;
                Interlocked.Exchange(ref _sequence, sampleIndex); Interlocked.Increment(ref _frames);
            }
        }
        catch (Exception ex) { _failure = ex; AppLog.Write("Audio encoding failed", ex); }
        finally { if (timerRequested) timeEndPeriod(1); }
    }
    public void Dispose()
    {
        _stop.Cancel(); _thread?.Join(); _source?.Dispose(); _stop.Dispose();
    }
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);
}
