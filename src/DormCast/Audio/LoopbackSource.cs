using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using DormCast.Hosting;

namespace DormCast.Audio;

public sealed class LoopbackSource(PcmRing ring, string? deviceId = null) : IDisposable
{
    readonly CancellationTokenSource _stop = new();
    Task? _worker;
    string _deviceName = "等待音频设备";
    string _status = "未启动";
    long _capturedSamples;
    long _lastPacket;
    double _peak;
    double _actualBufferMs;
    long _discontinuities;
    public string DeviceName => _deviceName;
    public string Status => _status;
    public long CapturedSamples => Interlocked.Read(ref _capturedSamples);
    public double Peak => LastPacket == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(LastPacket).TotalMilliseconds > 100 ? 0 : Volatile.Read(ref _peak);
    public double ActualBufferMs => Volatile.Read(ref _actualBufferMs);
    public long Discontinuities => Interlocked.Read(ref _discontinuities);
    public long LastPacket => Interlocked.Read(ref _lastPacket);

    public void Start() => _worker = Task.Factory.StartNew(CaptureLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    void CaptureLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = deviceId == null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(deviceId);
                var id = device.ID;
                using var client = device.AudioClient;
                using var packetReady = new EventWaitHandle(false, EventResetMode.AutoReset);
                var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
                client.Initialize(AudioClientShareMode.Shared,
                    AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                    200000, 0, format, Guid.Empty);
                client.SetEventHandle(packetReady.SafeWaitHandle.DangerousGetHandle());
                Volatile.Write(ref _actualBufferMs, client.BufferSize / 48.0);
                var capture = client.AudioCaptureClient;
                var buffer = new float[client.BufferSize * 2];
                ring.Clear();
                try
                {
                    client.Start();
                    _deviceName = device.FriendlyName;
                    _status = "共享回环 · 48kHz · 立体声";
                    var nextDeviceCheck = Environment.TickCount64 + 500;
                    while (!_stop.IsCancellationRequested)
                    {
                        packetReady.WaitOne(10);
                        while (!_stop.IsCancellationRequested && capture.GetNextPacketSize() > 0)
                        {
                            var pointer = capture.GetBuffer(out var frames, out var flags);
                            try
                            {
                                var count = checked(frames * 2);
                                if (count > buffer.Length) Array.Resize(ref buffer, count);
                                if (flags.HasFlag(AudioClientBufferFlags.Silent)) buffer.AsSpan(0, count).Clear();
                                else Marshal.Copy(pointer, buffer, 0, count);
                                if (flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity)) { ring.Clear(true); Interlocked.Increment(ref _discontinuities); }
                                double peak = 0;
                                for (var i = 0; i < count; i++)
                                {
                                    buffer[i] = float.IsFinite(buffer[i]) ? Math.Clamp(buffer[i], -1, 1) : 0;
                                    peak = Math.Max(peak, Math.Abs(buffer[i]));
                                }
                                ring.Write(buffer.AsSpan(0, count));
                                Volatile.Write(ref _peak, peak);
                                Interlocked.Add(ref _capturedSamples, frames);
                                Interlocked.Exchange(ref _lastPacket, System.Diagnostics.Stopwatch.GetTimestamp());
                            }
                            finally { capture.ReleaseBuffer(frames); }
                        }
                        if (Environment.TickCount64 >= nextDeviceCheck)
                        {
                            using var current = deviceId == null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(deviceId);
                            if (current.ID != id || device.State != DeviceState.Active) break;
                            nextDeviceCheck = Environment.TickCount64 + 500;
                        }
                    }
                }
                finally
                {
                    // Device removal can make Stop itself throw. Keep it inside our worker's exception boundary.
                    try { client.Stop(); } catch (Exception ex) { AppLog.Write("WASAPI stop on removed endpoint", ex); }
                }
            }
            catch (Exception ex)
            {
                _status = "设备暂不可用，正在重试";
                _deviceName = "等待默认输出设备";
                AppLog.Write("WASAPI capture retry", ex);
            }
            finally { ring.Clear(); Volatile.Write(ref _peak, 0); }
            if (_stop.Token.WaitHandle.WaitOne(500)) break;
        }
        _status = "已释放";
    }
    public void Dispose()
    {
        _stop.Cancel(); _worker?.GetAwaiter().GetResult(); _stop.Dispose();
    }
}
