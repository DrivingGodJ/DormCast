using DormCast.Hosting;
using NAudio.CoreAudioApi;

namespace DormCast.Audio;

// VB-CABLE loopback is pre-endpoint-volume. Mirror the Windows default (virtual)
// endpoint and the physical speaker, then apply its dB gain before network encoding.
public sealed class BroadcastVolumeLink(AudioEngine audio, string captureId, string outputId) : IDisposable
{
    readonly CancellationTokenSource _stop = new();
    Thread? _worker;
    public void Start() { _worker = new Thread(Run) { IsBackground = true, Name = "DormCast broadcast volume" }; _worker.Start(); }
    void Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var e = new MMDeviceEnumerator(); using var source = e.GetDevice(captureId); using var speaker = e.GetDevice(outputId);
                var s = source.AudioEndpointVolume; var p = speaker.AudioEndpointVolume;
                var original = s.MasterVolumeLevelScalar; var originalMute = s.Mute;
                var level = p.MasterVolumeLevelScalar; var muted = p.Mute;
                s.MasterVolumeLevelScalar = level; s.Mute = muted;
                try
                {
                    do
                    {
                        var fromSystem = s.MasterVolumeLevelScalar; var systemMute = s.Mute;
                        var fromSpeaker = p.MasterVolumeLevelScalar; var speakerMute = p.Mute;
                        // System volume keys win if both endpoints changed in the same polling interval.
                        if (Math.Abs(fromSystem - level) > 0.0005f || systemMute != muted)
                        { p.MasterVolumeLevelScalar = fromSystem; p.Mute = systemMute; }
                        else if (Math.Abs(fromSpeaker - level) > 0.0005f || speakerMute != muted)
                        { s.MasterVolumeLevelScalar = fromSpeaker; s.Mute = speakerMute; }
                        level = s.MasterVolumeLevelScalar; muted = s.Mute;
                        audio.SetMasterGain(muted ? 0 : GainFromDb(p.MasterVolumeLevel));
                    } while (!_stop.Token.WaitHandle.WaitOne(50));
                }
                finally
                {
                    // Restore only our virtual endpoint; preserve the user's final speaker setting.
                    if (Math.Abs(s.MasterVolumeLevelScalar - level) < 0.001f && s.Mute == muted)
                    { s.MasterVolumeLevelScalar = original; s.Mute = originalMute; }
                }
            }
            catch (Exception ex) { AppLog.Write("Broadcast volume endpoint retry", ex); _stop.Token.WaitHandle.WaitOne(1000); }
        }
    }
    public static float GainFromDb(float db) => float.IsFinite(db) ? (float)Math.Clamp(Math.Pow(10, db / 20.0), 0, 1) : 0;
    public void Dispose() { _stop.Cancel(); _worker?.Join(); _stop.Dispose(); }
}
