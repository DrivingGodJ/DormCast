using System.Text.Json;
using Concentus;
using DormCast.Audio;
using DormCast.Hosting;
using NAudio.CoreAudioApi;
using NAudio.Wave;

static class VolumeLinkChecks
{
    public static async Task RunAsync(byte[] stimulus)
    {
        var options = BroadcastOptions.Load(); AudioDevices.ValidateRoute(options.CaptureDeviceId, options.OutputDeviceId);
        using var e = new MMDeviceEnumerator(); using var source = e.GetDevice(options.CaptureDeviceId!); using var speaker = e.GetDevice(options.OutputDeviceId!);
        var s = source.AudioEndpointVolume; var p = speaker.AudioEndpointVolume; var sourceLevel = s.MasterVolumeLevelScalar; var sourceMute = s.Mute; var physicalLevel = p.MasterVolumeLevelScalar; var physicalMute = p.Mute;
        if (physicalMute || physicalLevel < 0.02f) { Console.WriteLine("SKIP volume-link audible test because the user muted the physical output"); return; }
        using var engine = new AudioEngine(20, options.CaptureDeviceId) { EncodeLocalMonitor = true };
        using var wire = OpusCodecFactory.CreateDecoder(48000, 2); using var local = OpusCodecFactory.CreateDecoder(48000, 2); var samples = new float[1920];
        double wireEnergy = 0, localEnergy = 0; long frames = 0; var measure = 0; var gate = new object();
        engine.FrameReady += frame => { lock (gate) { var count = wire.Decode(frame.Payload, samples, 960, false); if (measure == 1) for (var i=0;i<count*2;i++) wireEnergy += samples[i]*samples[i]; count = local.Decode(frame.MonitorPayload!, samples, 960, false); if (measure == 1) { for(var i=0;i<count*2;i++) localEnergy += samples[i]*samples[i]; frames++; } } };
        try
        {
            engine.Start();
            using (var link = new BroadcastVolumeLink(engine, source.ID, speaker.ID))
            {
                link.Start(); await Task.Delay(350); var changed = Math.Max(0.01f, physicalLevel * 0.9f); s.MasterVolumeLevelScalar = changed; await Task.Delay(250);
                if (Math.Abs(p.MasterVolumeLevelScalar - changed) > 0.002) throw new Exception("System volume did not reach physical speaker");
                p.MasterVolumeLevelScalar = physicalLevel; await Task.Delay(250);
                if (Math.Abs(s.MasterVolumeLevelScalar - physicalLevel) > 0.002) throw new Exception("Physical speaker control did not reach default system endpoint");
                using var data = new MemoryStream(stimulus); using var reader = new WaveFileReader(data); using var output = new WasapiOut(source, AudioClientShareMode.Shared, true, 20); output.Init(reader); output.Play(); await Task.Delay(500);
                s.Mute = true; await Task.Delay(500); lock (gate) measure = 1; await Task.Delay(700); lock (gate) measure = 0;
                if (!p.Mute || wireEnergy > 0.00001 || localEnergy < 0.00001 || frames < 15) throw new Exception($"Mute binding failed: wire={wireEnergy}, local={localEnergy}, frames={frames}");
                s.Mute = false; await Task.Delay(150);
                Directory.CreateDirectory("test-results/v0.5"); File.WriteAllText("test-results/v0.5/volume-link.json", JsonSerializer.Serialize(new { systemToPhysical=true,physicalToSystem=true, mutedNetworkEnergy=wireEnergy, unscaledLocalEnergy=localEnergy, frames, actualCapture=engine.CaptureStatus }));
                Console.WriteLine("PASS actual VB-CABLE system↔speaker volume/mute; encoded network silent while local pre-endpoint audio stays unscaled");
            }
        }
        finally { p.MasterVolumeLevelScalar = physicalLevel; p.Mute = physicalMute; s.MasterVolumeLevelScalar = sourceLevel; s.Mute = sourceMute; }
    }
}
