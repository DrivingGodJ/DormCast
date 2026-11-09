using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Concentus;
using DormCast.Audio;
using DormCast.Hosting;
using DormCast.Networking;
using DormCast.Synchronization;
using NAudio.Wave;

static void Check(bool test, string message) { if (!test) throw new Exception(message); Console.WriteLine($"PASS {message}"); }
static byte[] MakeStimulus(double seconds)
{
    using var data = new MemoryStream();
    using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(data), new WaveFormat(48000, 16, 2)))
    {
        for (var i = 0; i < seconds * 48000; i++)
        {
            var envelope = Math.Min(1, Math.Min(i / 2400.0, (seconds * 48000 - i) / 2400.0));
            writer.WriteSample((float)(0.06 * envelope * Math.Sin(2 * Math.PI * 997 * i / 48000)));
            writer.WriteSample((float)(0.06 * envelope * Math.Sin(2 * Math.PI * 1499 * i / 48000)));
        }
    }
    return data.ToArray();
}

if (args.Contains("--devices"))
{
    Console.WriteLine(JsonSerializer.Serialize(new { defaultId = AudioDevices.DefaultRenderId(), devices = AudioDevices.RenderDevices() }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args.Contains("--volume-link-check")) { await VolumeLinkChecks.RunAsync(MakeStimulus(3)); return; }
if (args.Contains("--feature-checks")) { await FeatureChecks.RunAsync(); return; }
if (args.Contains("--dual-join-check")) { await DualJoinChecks.RunAsync(); return; }
if (args.Contains("--volume-gain-check")) { await VolumeGainChecks.RunAsync(args.Contains("--stimulus") ? MakeStimulus(4) : null); return; }
if (args.Contains("--github-release-check"))
{
    using var updater = new DormCast.Updates.GitHubReleaseClient();
    var release = await updater.CheckAsync(new Version(0, 4, 0)) ?? throw new Exception("No newer stable public release found");
    var current = await updater.CheckAsync(release.Version); Check(current == null, "live latest release compares equal to the installed version");
    var download = await updater.DownloadAsync(release, Path.GetFullPath("test-results/v0.5/live-update-download"));
    Check(new FileInfo(download).Length == release.Size, "live official installer downloaded anonymously and SHA-256 verified, without launching");
    File.Delete(download); Console.WriteLine(JsonSerializer.Serialize(new { release.Version, release.Size, release.Sha256 })); return;
}
if (args.Contains("--probe-active-loopback-mute"))
{
    var id = BroadcastOptions.Load().CaptureDeviceId ?? throw new Exception("No configured virtual capture device");
    using var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator(); using var device = devices.GetDevice(id);
    if (!device.FriendlyName.Contains("CABLE Input")) throw new Exception("Probe only the configured VB-CABLE source");
    var mute = device.AudioEndpointVolume.Mute; if (mute) throw new Exception("Source is already muted; leave it untouched");
    var pcm = new PcmRing(48000 * 2); using var capture = new LoopbackSource(pcm, id); capture.Start();
    async Task<double> Rms() { pcm.Clear(false); var buffer = new float[9600]; double energy = 0; long total = 0; for (var n = 0; n < 15; n++) { await Task.Delay(25); var count = pcm.Read(buffer); for (var i = 0; i < count; i++) energy += buffer[i] * buffer[i]; total += count; } return total > 0 ? Math.Sqrt(energy / total) : 0; }
    try { await Task.Delay(250); var normal = await Rms(); device.AudioEndpointVolume.Mute = true; await Task.Delay(150); var muted = await Rms(); Directory.CreateDirectory("test-results/v0.5"); var result = new {normal, muted, captured=capture.CapturedSamples, status=capture.Status, muteRatio=normal>0.0001?(double?)(muted/normal):null}; File.WriteAllText("test-results/v0.5/active-mute-probe.json",JsonSerializer.Serialize(result));Console.WriteLine(JsonSerializer.Serialize(result)); }
    finally { device.AudioEndpointVolume.Mute = mute; }
    return;
}
if (args.Contains("--metadata"))
{
    await using var music = new NowPlayingService(); music.Start(); await Task.Delay(3500);
    var info = music.Current; var cover = info.CoverRevision == null ? null : music.Cover(info.CoverRevision);
    Console.WriteLine(JsonSerializer.Serialize(new { info, coverBytes = cover?.Bytes.Length, coverType = cover?.ContentType }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args.Contains("--speaker-volume"))
{
    var selected = SpeakerVolume.Read(BroadcastOptions.Load().OutputDeviceId) ?? throw new Exception("No physical speaker found");
    SpeakerVolume.Set(selected.DeviceId, selected.Level);
    var readBack = SpeakerVolume.Read(selected.DeviceId)!;
    Check(Math.Abs(readBack.Level - selected.Level) < 0.001, "physical speaker volume round trip targets the selected endpoint");
    if (selected.Muted) { using var e = new NAudio.CoreAudioApi.MMDeviceEnumerator(); using var d = e.GetDevice(selected.DeviceId); d.AudioEndpointVolume.Mute = true; }
    var cable = AudioDevices.RenderDevices().FirstOrDefault(d => d.IsVirtual);
    if (cable != null)
    {
        try { SpeakerVolume.Set(cable.Id, 0.5f); throw new Exception("Virtual capture volume accepted"); }
        catch (ArgumentException) { Console.WriteLine("PASS virtual capture endpoint is excluded from speaker volume control"); }
    }
    Console.WriteLine(JsonSerializer.Serialize(readBack)); return;
}
if (args.Contains("--volume-performance"))
{
    using var controller = new SpeakerVolumeController(BroadcastOptions.Load().OutputDeviceId);
    var deadline = Stopwatch.GetTimestamp();
    while (controller.Current == null && Stopwatch.GetElapsedTime(deadline).TotalSeconds < 5) await Task.Delay(50);
    var original = controller.Current ?? throw new Exception("No physical speaker available");
    if (original.Muted) { Console.WriteLine("SKIP actual writes because the speaker is muted"); return; }
    var began = Stopwatch.GetTimestamp();
    for (var i = 0; i < 100000; i++) controller.RequestLevel(original.Level);
    var inputMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds; await Task.Delay(700);
    Check(inputMs < 100 && controller.DeviceOpenCount == 1 && controller.WriteCount < 20, "100000 volume requests remain nonblocking and coalesce without reopening the endpoint");
    Directory.CreateDirectory("test-results/winui");
    File.WriteAllText("test-results/winui/volume-performance.json", JsonSerializer.Serialize(new { requests = 100000, inputMs, deviceOpenCount = controller.DeviceOpenCount, actualWrites = controller.WriteCount, endpoint = controller.Current?.Name }));
    return;
}
if (args.Contains("--probe-output"))
{
    var index = Array.IndexOf(args, "--probe-output");
    var outputId = args[index + 1]; var samples = new PcmRing(48000 * 2); using var capture = new LoopbackSource(samples, outputId);
    capture.Start(); var pcm = new float[9600]; double energy = 0; var began = Stopwatch.GetTimestamp();
    while (Stopwatch.GetElapsedTime(began).TotalSeconds < 7)
    {
        await Task.Delay(50); var count = samples.Read(pcm);
        for (var i = 0; i < count; i++) energy += pcm[i] * pcm[i];
    }
    Check(capture.CapturedSamples > 48000 && energy > 0.001, "native delayed speaker output captured as real nonzero WASAPI audio");
    Directory.CreateDirectory("test-results/sync");
    File.WriteAllText("test-results/sync/native-output.json", JsonSerializer.Serialize(new { outputId, capturedSamples = capture.CapturedSamples, energy, actualBufferMs = capture.ActualBufferMs }));
    return;
}
if (args.Contains("--stimulus"))
{
    using var data = new MemoryStream(MakeStimulus(6)); using var reader = new WaveFileReader(data); using var output = new WasapiOut();
    output.Init(reader); output.Play(); while (output.PlaybackState == PlaybackState.Playing) await Task.Delay(100);
    return;
}

var ring = new PcmRing(8);
await FeatureChecks.RunAsync();
Check(NowPlayingService.ImageType(new byte[] { 0xff, 0xd8, 0xff, 0 }) == "image/jpeg" && NowPlayingService.ImageType("<svg onload='bad'>"u8) == null, "cover sniffer rejects active SVG" );
var history = new TimelinePcmBuffer(8); history.Write(100, [0, 10, 2, 12, 4, 14]);
var interpolated = new float[4]; history.Read(100.5, 1, interpolated);
Check(interpolated.SequenceEqual(new float[] { 1, 11, 3, 13 }), "timestamped PCM history interpolates both stereo channels");
history.Read(0, 1, interpolated); Check(interpolated.All(x => x == 0), "timestamped history never replays overwritten or missing frames");
Check(TimelinePcmBuffer.Unwrap(48, 4294967296L + 48) == 4294967296L + 48, "source RTP timestamp wraps without resetting the playback timeline");
try { new BroadcastOptions(HostPlayback: true, CaptureDeviceId: "same", OutputDeviceId: "same").Validate(); throw new Exception("feedback route accepted"); }
catch (ArgumentException) { Console.WriteLine("PASS host capture/render feedback route rejected"); }
ring.Write([1, 2, 3, 4, 5, 6]); ring.Write([7, 8, 9, 10]);
var target = new float[8]; Check(ring.Read(target) == 8 && target.SequenceEqual(new float[] { 3, 4, 5, 6, 7, 8, 9, 10 }), "PCM overflow retains newest stereo samples");
Check(ring.DroppedSamples == 2, "PCM drop accounting");
Check(ring.Read(target) == 0 && target.All(x => x == 0), "PCM underrun produces silence without stale replay");
ring.Write(Enumerable.Range(0, 20).Select(i => (float)i).ToArray()); ring.Read(target);
Check(target.SequenceEqual(Enumerable.Range(12, 8).Select(i => (float)i)), "oversize capture packet retains bounded tail");
var nic = new LanInterface("test", "test", IPAddress.Parse("192.168.3.100"), IPAddress.Parse("255.255.255.0"), true, true);
Check(nic.Contains(IPAddress.Parse("192.168.3.5")) && !nic.Contains(IPAddress.Parse("192.168.4.5")) && !nic.Contains(IPAddress.Parse("8.8.8.8")), "LAN mask filters other subnets and public IPs");
Check(!LanInterface.IsPrivate(IPAddress.Parse("172.32.0.1")) && LanInterface.IsPrivate(IPAddress.Parse("172.16.0.1")), "private address bounds");
try { new BroadcastOptions(Port: 80).Validate(); throw new Exception("bad port accepted"); } catch (ArgumentException) { Console.WriteLine("PASS option boundary"); }

var reports = new List<object>();
foreach (var frameMs in new[] { 20, 10 })
{
    using var decoder = OpusCodecFactory.CreateDecoder(48000, 2);
    using var engine = new AudioEngine(frameMs);
    long decoded = 0; double energy = 0; uint previous = 0; bool monotonic = true;
    var frame = new float[960 * 2];
    engine.FrameReady += packet =>
    {
        if (decoded > 0 && unchecked(packet.RtpTimestamp - previous) < packet.Duration) monotonic = false;
        previous = packet.RtpTimestamp;
        var samples = decoder.Decode(packet.Payload, frame, 960, false);
        for (var i = 0; i < samples * 2; i++) energy += frame[i] * frame[i];
        decoded += samples;
    };
    engine.Start();
    await Task.Delay(600);
    using var data = new MemoryStream(MakeStimulus(2)); using var reader = new WaveFileReader(data); using var output = new WasapiOut();
    output.Init(reader); output.Play(); await Task.Delay(2400);
    var snapshot = JsonSerializer.SerializeToElement(engine.Snapshot());
    Check(engine.Failure == null, $"{frameMs}ms encoder runs");
    Check(snapshot.GetProperty("capturedSamples").GetInt64() > 48000, $"{frameMs}ms real WASAPI output captured");
    Check(decoded > 48000 && energy > 0.01, $"{frameMs}ms real loopback → Opus → decoded nonzero stereo PCM");
    Check(monotonic, $"{frameMs}ms RTP source timeline monotonic");
    reports.Add(new { frameMs, decodedSamples = decoded, decodedEnergy = energy, audio = snapshot });
}
Directory.CreateDirectory("test-results");
File.WriteAllText("test-results/audio-checks.json", JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));

var actual = LanInterface.Discover().FirstOrDefault();
if (actual != null)
{
    await using var host = new BroadcastHost();
    await host.StartAsync(new BroadcastOptions(Port: 18081), actual);
    using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
    var page = await http.GetAsync($"http://{actual.Address}:18081/"); Check(page.IsSuccessStatusCode, "LAN HTTP serves embedded client");
    using (var canonical = new HttpRequestMessage(HttpMethod.Get, $"http://{actual.Address}:18081/"))
    {
        canonical.Headers.Host = new Uri(host.JoinUrl!).Authority;
        Check((await http.SendAsync(canonical)).IsSuccessStatusCode, "persistent local hostname is accepted with subnet filtering intact");
    }
    Check((await http.GetAsync($"http://{actual.Address}:18081/cover/0123456789abcdef")).StatusCode == HttpStatusCode.Unauthorized, "artwork endpoint requires the join credential");
    Check((await http.GetAsync($"http://{actual.Address}:18081/media.js")).IsSuccessStatusCode, "embedded media helper is available");
    using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{actual.Address}:18081/"); request.Headers.Host = "attacker.example:18081";
    Check((await http.SendAsync(request)).StatusCode == HttpStatusCode.Forbidden, "Host header rejects DNS rebinding");
    using (var badOrigin = new System.Net.WebSockets.ClientWebSocket())
    {
        badOrigin.Options.SetRequestHeader("Origin", "http://other.example");
        try { await badOrigin.ConnectAsync(new Uri($"ws://{actual.Address}:18081/signal"), CancellationToken.None); throw new Exception("bad origin accepted"); }
        catch (System.Net.WebSockets.WebSocketException) { Console.WriteLine("PASS cross-origin WebSocket rejected"); }
    }
    using (var badToken = new System.Net.WebSockets.ClientWebSocket())
    {
        badToken.Options.SetRequestHeader("Origin", $"http://{actual.Address}:18081");
        await badToken.ConnectAsync(new Uri($"ws://{actual.Address}:18081/signal"), CancellationToken.None);
        await badToken.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "hello", token = new string('0', 64) }), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        var close = await badToken.ReceiveAsync(new byte[1024], CancellationToken.None);
        Check(close.CloseStatus == System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation, "invalid session token rejected");
        await badToken.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
    await using (var occupied = new BroadcastHost())
    {
        try { await occupied.StartAsync(new BroadcastOptions(Port: 18081), actual); throw new Exception("occupied port accepted"); }
        catch (IOException) { Check(!occupied.Running, "failed start rolls back capture and listener"); }
    }
    var originalLink = host.JoinUrl;
    await host.StopAsync();
    using var rebind = new System.Net.Sockets.TcpListener(actual.Address, 18081); rebind.Start(); rebind.Stop();
    Check(true, "stop releases TCP port");
    await host.StartAsync(new BroadcastOptions(Port: 18081), actual);
    Check(host.JoinUrl == originalLink, "stop and restart preserve the complete bookmarked join URL"); await host.StopAsync();
    Check(true, "start → stop → restart lifecycle");
}
Console.WriteLine("All checks passed. Physical multi-device acoustic latency remains unmeasured.");
