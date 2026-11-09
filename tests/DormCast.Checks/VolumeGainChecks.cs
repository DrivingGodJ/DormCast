using System.Text.Json;
using Concentus;
using DormCast.Audio;
using DormCast.Hosting;
using NAudio.CoreAudioApi;
using NAudio.Wave;

static class VolumeGainChecks
{
    // Read actual loopback and compare the simultaneous network/local decoded packets.
    // Endpoint volume and defaults stay untouched; an explicit fixture can provide audio.
    public static async Task RunAsync(byte[]? stimulus = null)
    {
        var options = BroadcastOptions.Load();
        var devices = AudioDevices.RenderDevices();
        var captureId = devices.Any(d => d.Id == options.CaptureDeviceId) ? options.CaptureDeviceId : devices.FirstOrDefault(d => d.IsVirtual && d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))?.Id;
        if (captureId == null) throw new Exception("No active virtual capture endpoint for the gain probe");
        using var e = new MMDeviceEnumerator(); using var speaker = e.GetDevice(options.OutputDeviceId!);
        var expected = BroadcastVolumeLink.NetworkGain(speaker.AudioEndpointVolume.MasterVolumeLevelScalar, speaker.AudioEndpointVolume.Mute);
        if (expected < 0.02f) throw new Exception("Read-only gain check needs an unmuted source at 2% or above");
        using var wire = OpusCodecFactory.CreateDecoder(48000, 2); using var local = OpusCodecFactory.CreateDecoder(48000, 2);
        using var source = e.GetDevice(captureId);
        using var data = stimulus == null ? null : new MemoryStream(stimulus);
        using var reader = data == null ? null : new WaveFileReader(data);
        using var output = reader == null ? null : new WasapiOut(source, AudioClientShareMode.Shared, true, 20);
        if (reader != null) output!.Init(reader);
        using var engine = new AudioEngine(20, captureId) { EncodeLocalMonitor = true };
        engine.SetMasterGain(expected);
        var pcm = new float[1920]; double wireEnergy = 0, localEnergy = 0; long frames = 0; var measuring = false; var gate = new object();
        engine.FrameReady += packet => { lock (gate) { var count=wire.Decode(packet.Payload,pcm,960,false);if(measuring)for(var i=0;i<count*2;i++)wireEnergy+=pcm[i]*pcm[i];count=local.Decode(packet.MonitorPayload!,pcm,960,false);if(measuring){for(var i=0;i<count*2;i++)localEnergy+=pcm[i]*pcm[i];frames++;} } };
        engine.Start(); output?.Play(); await Task.Delay(1000); lock(gate)measuring=true; await Task.Delay(2500); lock(gate)measuring=false;
        if(engine.Failure!=null)throw engine.Failure;
        if(localEnergy<0.001)throw new Exception("No live source audio to measure; leave endpoint volumes untouched");
        var ratio=Math.Sqrt(wireEnergy/localEnergy);
        if(frames<70 || Math.Abs(ratio/expected-1)>0.2)throw new Exception($"Decoded gain mismatch: expected {expected}, actual {ratio}, frames {frames}");
        Directory.CreateDirectory("test-results/v0.5");File.WriteAllText("test-results/v0.5/percentage-gain.json",JsonSerializer.Serialize(new{expected,measuredAmplitudeRatio=ratio,frames,wireEnergy,localEnergy,actualCapture=engine.CaptureStatus}));
        Console.WriteLine($"PASS real WASAPI → Opus network/local amplitude ratio {ratio:0.000} matches volume percentage {expected:0.000}; endpoint volumes untouched");
    }
}
