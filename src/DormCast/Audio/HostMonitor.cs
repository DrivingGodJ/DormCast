using System.Runtime.InteropServices;
using Concentus;
using DormCast.Hosting;
using DormCast.Synchronization;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DormCast.Audio;

public sealed class HostMonitor(AudioEngine engine, string outputId, SyncCoordinator sync, int hardwareCorrectionMs) : IDisposable
{
    readonly TimelinePcmBuffer _history = new();
    readonly CancellationTokenSource _stop = new();
    readonly object _decodeGate = new();
    readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(48000, 2);
    readonly float[] _decoded = new float[960 * 2];
    Task? _worker;
    bool _disposed;
    string _status = "准备主机同步播放";
    double _outputMs, _alignmentErrorMs;
    long _decodedSamples;
    public string Status => _status;
    public object Snapshot() => new { status = _status, estimatedOutputMs = _outputMs, hardwareCorrectionMs, alignmentErrorMs = _alignmentErrorMs, decodedSamples = Interlocked.Read(ref _decodedSamples) };
    public void Start()
    {
        engine.FrameReady += Receive;
        _worker = Task.Factory.StartNew(RenderLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    void Receive(EncodedFrame frame)
    {
        lock (_decodeGate)
        {
            if (_disposed) return;
            // Decode the same Opus packets so encoder lookahead matches the browsers' content timeline.
            var count = _decoder.Decode(frame.MonitorPayload ?? frame.Payload, _decoded, 960, false);
            var sample = TimelinePcmBuffer.Unwrap(frame.RtpTimestamp, (long)((engine.NowMs - AudioEngine.TimelineStartMs) * 48));
            _history.Write(sample, _decoded.AsSpan(0, count * 2));
            Interlocked.Add(ref _decodedSamples, count);
        }
    }
    void RenderLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var e = new MMDeviceEnumerator(); using var device = e.GetDevice(outputId); using var client = device.AudioClient;
                using var ready = new EventWaitHandle(false, EventResetMode.AutoReset);
                client.Initialize(AudioClientShareMode.Shared,
                    AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                    200000, 0, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), Guid.Empty);
                client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
                var latency = client.StreamLatency / 10000.0;
                var render = client.AudioRenderClient; var buffer = new float[client.BufferSize * 2];
                _outputMs = latency + client.BufferSize / 96.0 + hardwareCorrectionMs;
                sync.SetHostLatency(_outputMs);
                var cursor = double.NaN;
                try
                {
                    client.Start(); _status = $"主机同步播放 · {device.FriendlyName}";
                    while (!_stop.IsCancellationRequested)
                    {
                        ready.WaitOne(10);
                        var padding = client.CurrentPadding; var frames = client.BufferSize - padding;
                        if (frames <= 0) continue;
                        var common = sync.GetState(engine.NowMs).PlayoutTargetMs;
                        var desired = (engine.NowMs + padding / 48.0 + latency + hardwareCorrectionMs - AudioEngine.TimelineStartMs - common) * 48;
                        var discontinuity = double.IsNaN(cursor) || Math.Abs(desired - cursor) > 48000 / 4;
                        if (discontinuity) cursor = desired;
                        _alignmentErrorMs = (cursor - desired) / 48;
                        // Small rate correction avoids repeatedly skipping samples at render-buffer boundaries.
                        var step = 1 + Math.Clamp((desired - cursor) / 48000, -0.02, 0.02);
                        cursor = _history.Read(cursor, step, buffer.AsSpan(0, frames * 2));
                        if (discontinuity) for (var i = 0; i < Math.Min(frames, 240); i++)
                        { buffer[i * 2] *= i / 240f; buffer[i * 2 + 1] *= i / 240f; }
                        var pointer = render.GetBuffer(frames);
                        try { Marshal.Copy(buffer, 0, pointer, frames * 2); }
                        finally { render.ReleaseBuffer(frames, AudioClientBufferFlags.None); }
                    }
                }
                finally { try { client.Stop(); } catch (Exception ex) { AppLog.Write("Host monitor stop", ex); } }
            }
            catch (Exception ex) { _status = "主机扬声器暂不可用，正在重连"; AppLog.Write("Host monitor retry", ex); }
            if (_stop.Token.WaitHandle.WaitOne(500)) break;
        }
        _status = "主机同步播放已停止";
    }
    public void Dispose()
    {
        engine.FrameReady -= Receive;
        lock (_decodeGate) _disposed = true;
        _stop.Cancel(); _worker?.GetAwaiter().GetResult(); _decoder.Dispose(); _stop.Dispose();
    }
}
