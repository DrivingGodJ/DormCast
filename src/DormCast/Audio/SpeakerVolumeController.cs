using System.Diagnostics;
using DormCast.Hosting;
using NAudio.CoreAudioApi;

namespace DormCast.Audio;

// CoreAudio objects stay on this worker; UI input only replaces the latest desired value.
public sealed class SpeakerVolumeController : IDisposable
{
    readonly CancellationTokenSource _stop = new();
    readonly Thread _worker;
    string? _preferred;
    SpeakerVolumeState? _current;
    int _pending = -1;
    long _opens, _writes;
    public SpeakerVolumeState? Current => Volatile.Read(ref _current);
    public long DeviceOpenCount => Interlocked.Read(ref _opens);
    public long WriteCount => Interlocked.Read(ref _writes);
    public SpeakerVolumeController(string? preferredId)
    {
        _preferred = preferredId;
        _worker = new Thread(Run) { IsBackground = true, Name = "DormCast speaker volume" }; _worker.Start();
    }
    public void Configure(string? preferredId) => Volatile.Write(ref _preferred, preferredId);
    public void RequestLevel(float level)
    {
        if (!float.IsFinite(level) || level is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(level));
        Interlocked.Exchange(ref _pending, (int)Math.Round(level * 10000));
    }
    void Run()
    {
        MMDeviceEnumerator? enumerator = null; MMDevice? device = null;
        string? configured = null, name = null; long lastResolve = 0, lastRead = 0;
        try
        {
            enumerator = new MMDeviceEnumerator();
            while (!_stop.Token.WaitHandle.WaitOne(50))
            {
                try
                {
                    var preferred = Volatile.Read(ref _preferred);
                    if (device == null || configured != preferred || Stopwatch.GetElapsedTime(lastResolve).TotalSeconds >= 5)
                    {
                        var selected = SpeakerVolume.Read(preferred);
                        if (selected?.DeviceId != device?.ID)
                        {
                            device?.Dispose(); device = selected == null ? null : enumerator.GetDevice(selected.DeviceId);
                            if (device != null) Interlocked.Increment(ref _opens);
                        }
                        name = selected?.Name; configured = preferred; lastResolve = Stopwatch.GetTimestamp();
                    }
                    var pending = Interlocked.Exchange(ref _pending, -1);
                    if (device == null) { Volatile.Write(ref _current, null); continue; }
                    if (pending >= 0)
                    {
                        device.AudioEndpointVolume.MasterVolumeLevelScalar = pending / 10000f;
                        if (pending > 0) device.AudioEndpointVolume.Mute = false;
                        Interlocked.Increment(ref _writes);
                    }
                    if (pending >= 0 || Stopwatch.GetElapsedTime(lastRead).TotalMilliseconds >= 400)
                    {
                        Volatile.Write(ref _current, new SpeakerVolumeState(device.ID, name ?? "实际扬声器", device.AudioEndpointVolume.MasterVolumeLevelScalar, device.AudioEndpointVolume.Mute));
                        lastRead = Stopwatch.GetTimestamp();
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("Speaker volume device unavailable", ex); device?.Dispose(); device = null;
                    Volatile.Write(ref _current, null); _stop.Token.WaitHandle.WaitOne(1000);
                }
            }
        }
        finally { device?.Dispose(); enumerator?.Dispose(); }
    }
    public void Dispose() { _stop.Cancel(); _worker.Join(); _stop.Dispose(); }
}
