using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DormCast.Audio;
using DormCast.Hosting;
using DormCast.Networking;
using DormCast.Synchronization;
using DormCast.Updates;

static class FeatureChecks
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    public static async Task RunAsync()
    {
        var legacy = new JoinIdentity("dormcast-0123456789abcdef.local", new string('a', 64));
        var migrated = JoinIdentity.Resolve(legacy, null, "machineA");
        Check(migrated.HostName == legacy.HostName && migrated.Token == legacy.Token, "legacy full join link survives migration");
        Check(JoinIdentity.Resolve(null, migrated, "machineA") == migrated, "registry backup recovers a removed application-data identity");
        var other = JoinIdentity.Resolve(migrated, migrated, "machineB");
        Check(other.HostName != migrated.HostName && other.Token != migrated.Token, "copied machine profile does not clone another host's join identity");
        Check(!JoinIdentity.IsValid(new JoinIdentity(null!, null!)), "malformed identity is rejected safely");
        var room = new SyncCoordinator();
        Check(room.GetState(0).TargetMs == 250, "common delay starts at the 250ms minimum");
        for (var t = 0; t <= 30000; t += 1000) { room.Report("a", "fast", 100 + (t % 3000 - 1000) / 50.0, 2, true, t); if (room.GetState(t).TargetMs != 250) throw new Exception("Noise changed target"); } Check(room.GetState(30000).TargetMs == 250, "short path jitter leaves the target fixed");
        Check(!room.Report("bad", "bad", double.NaN, 1, true, 30000) && !room.Report("bad", "bad", 900, 80, true, 30000), "invalid and uncertain reports cannot inflate the target");
        var trace = new List<SyncState>();
        for (var t = 31000; t <= 70000; t += 1000) { room.Report("b", "slow", t == 32000 ? 1000 : 320, 2, true, t); var state = room.GetState(t); trace.Add(state); if (t < 39000) Check(state.TargetMs == 250, "slow path requires a sustained observation before increasing delay"); }
        Check(room.GetState(70000).TargetMs == 400 && trace.Any(s => s.Direction == "increasing"), "sustained slower device selects a 50ms bucket and gradual playout change");
        Check(trace.Zip(trace.Skip(1)).All(p => Math.Abs(p.First.PlayoutTargetMs - p.Second.PlayoutTargetMs) <= 20.1), "actual playout target changes by at most 20ms per second");
        room.Remove("b"); room.GetState(71000); Check(room.GetState(90000).TargetMs == 400, "slow device exit does not immediately reduce delay");
        for (var t = 91000; t <= 150000; t += 1000) { room.GetState(t); for (var i = 1; i < 10; i++) room.GetState(t + i); }
        Check(room.GetState(150010).TargetMs == 250, "delay automatically decreases after the 60-second stability period, with frequent UI reads");
        var background = new SyncCoordinator(); for (var t = 0; t <= 12000; t += 1000) { background.Report("p", "phone", 400, 1, true, t); background.SetBackground("p", true); background.GetState(t); }
        Check(background.GetState(60000).MeasuredDevices == 1 && background.GetState(60000).TargetMs == 500, "background timer suspension preserves a connected device's floor");
        var hold = new HostCompensation(); hold.SetDesired(160); Check(hold.AppliedMs == 160, "initial sender hold is seeded before the client becomes audible"); hold.SetDesired(20);
        for (var t = 0; t <= 1000; t += 50) hold.Advance(t);
        Check(hold.AppliedMs is >= 115 and <= 125, $"per-device sender compensation converges at NetEQ-safe 40ms/s (at 1s: {hold.AppliedMs:F1}ms)");
        for (var t = 1050; t <= 4000; t += 50) hold.Advance(t);
        Check(Math.Abs(hold.AppliedMs - 20) < 0.1, "sender compensation finishes converging smoothly without browser timers");
        var sampleDevices = new[]
        {
            new AudioDeviceChoice("v1", "VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)"),
            new AudioDeviceChoice("v2", "CABLE Input (VB-Audio Virtual Cable)"),
            new AudioDeviceChoice("p1", "扬声器 (Realtek(R) Audio)"),
            new AudioDeviceChoice("p2", "耳机 (USB Audio)")
        };
        var sampleInputs = new[]
        {
            new AudioDeviceChoice("i1", "MIXLINE Stream", CaptureEndpointFlow.CaptureInput),
            new AudioDeviceChoice("i2", "麦克风 (USB Mic)", CaptureEndpointFlow.CaptureInput)
        };
        Check(AudioDevices.FindVirtualCaptureDevice(sampleDevices)?.Id == "v2", "auto virtual cable selection prefers CABLE Input");
        Check(AudioDevices.FindVirtualCaptureDevice(sampleDevices.Where(d => !d.IsVirtual)) == null, "missing virtual cable is detected so installer/guide can prompt");
        var captureList = AudioDevices.CaptureDevices(sampleDevices, sampleInputs);
        Check(captureList.Count == 4 && captureList[0].Id == "v2" && captureList.Any(d => d.Id == "i1" && d.IsCaptureInput), "capture device list includes virtual cables and custom input endpoints");
        Check(AudioDevices.ResolveCaptureDevice("i1", sampleDevices, sampleInputs)?.Id == "i1", "capture device resolver honors saved custom input endpoint");
        Check(AudioDevices.ResolveCaptureDevice(null, sampleDevices, sampleInputs)?.Id == "v2", "capture device resolver prefers CABLE Input when nothing is saved");
        Check(AudioDevices.ResolveCaptureDevice(null, sampleDevices.Where(d => !d.IsVirtual), sampleInputs) == null, "capture device resolver never auto-picks a microphone when VB-CABLE is missing");
        Check(AudioDevices.ValidateRoute("i1", "p2", hostPlayback: true, sampleDevices, sampleInputs).Id == "i1", "custom input endpoint validates cleanly with host playback enabled");
        Check(AudioDevices.ValidateRoute("i1", null, hostPlayback: false, sampleDevices, sampleInputs).Id == "i1", "custom input endpoint validates without physical output when host playback is off");
        try { AudioDevices.ValidateRoute("i1", null, hostPlayback: true, sampleDevices, sampleInputs); throw new Exception("host playback without output accepted"); } catch (ArgumentException) { Console.WriteLine("PASS host playback requires real output device"); }
        try { new BroadcastOptions(SyncEnabled: false, HostPlayback: true, CaptureDeviceId: "i1", OutputDeviceId: "p1").Validate(); throw new Exception("unsynced host playback accepted"); } catch (ArgumentException) { Console.WriteLine("PASS computer sound enforces unified sync"); }
        var physicalOnly = AudioDevices.PhysicalOutputDevices(sampleDevices);
        Check(physicalOnly.Count == 2 && physicalOnly.All(d => !d.IsVirtual), "selectable outputs include only physical audio devices");
        Check(AudioDevices.ResolvePhysicalOutput("p2", sampleDevices, "p1")?.Id == "p2" && AudioDevices.ResolvePhysicalOutput("v2", sampleDevices, "p1")?.Id == "p1", "physical output resolver ignores virtual devices and honors saved physical output");
        Check(BroadcastVolumeLink.NetworkGain(0.14f, false) == 0.14f && BroadcastVolumeLink.NetworkGain(0.5f, false) == 0.5f && BroadcastVolumeLink.NetworkGain(1, false) == 1, "network amplitude follows Windows percentage without importing the physical speaker's dB taper");
        Check(BroadcastVolumeLink.NetworkGain(0.7f, true) == 0 && BroadcastVolumeLink.NetworkGain(0, false) == 0 && BroadcastVolumeLink.NetworkGain(float.NaN, false) == 0 && BroadcastVolumeLink.NetworkGain(2, false) == 1, "network mute and gain bounds prevent unintended amplification");
        Directory.CreateDirectory("test-results/v0.5"); File.WriteAllText("test-results/v0.5/stable-delay.json", JsonSerializer.Serialize(trace));
        await UpdateChecks();
    }
    static async Task UpdateChecks()
    {
        var payload = Encoding.UTF8.GetBytes("verified update fixture, never executed"); var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        string Release(string url = "https://github.com/DrivingGodJ/DormCast/releases/download/v0.5.0/DormCast-0.5.0-Setup-x64.exe", bool prerelease = false) => JsonSerializer.Serialize(new { draft = false, prerelease, tag_name = "v0.5.0", body = "Fixture", assets = new[] { new { name = "DormCast-0.5.0-Setup-x64.exe", size = payload.Length, browser_download_url = url, digest = "sha256:" + digest } } });
        var update = GitHubReleaseClient.Parse(Encoding.UTF8.GetBytes(Release()), new Version(0, 4, 0))!;
        Check(update.Version == new Version(0, 5, 0) && GitHubReleaseClient.Parse(Encoding.UTF8.GetBytes(Release()), new Version(0, 5, 0)) == null, "release comparison handles newer and current versions numerically");
        var appVer = DormCast.Hosting.BroadcastHost.AppVersion;
        Check(appVer.Revision == -1 && appVer >= new Version(0, 7, 4), $"assembly AppVersion is normalized 3-part version ({appVer})");
        var selfRelease = JsonSerializer.Serialize(new { draft = false, prerelease = false, tag_name = $"v{appVer}", body = "Self", assets = new[] { new { name = $"DormCast-{appVer}-Setup-x64.exe", size = payload.Length, browser_download_url = $"https://github.com/DrivingGodJ/DormCast/releases/download/v{appVer}/DormCast-{appVer}-Setup-x64.exe", digest = "sha256:" + digest } } });
        Check(GitHubReleaseClient.Parse(Encoding.UTF8.GetBytes(selfRelease), appVer) == null, "current release does not prompt to update to itself");
        Check(GitHubReleaseClient.Parse(Encoding.UTF8.GetBytes(Release(prerelease: true)), new Version(0, 4, 0)) == null, "automatic updater ignores prereleases");
        try { GitHubReleaseClient.Parse(Encoding.UTF8.GetBytes(Release("https://example.com/bad.exe")), new Version(0, 4, 0)); throw new Exception("Foreign asset accepted"); } catch (InvalidDataException) { Console.WriteLine("PASS foreign update URL rejected"); }
        using var http = new HttpClient(new FixtureHandler(payload)); using var client = new GitHubReleaseClient(http);
        using var limitedHttp = new HttpClient(new RateLimitHandler(Encoding.UTF8.GetBytes(Release()))); using var limitedClient = new GitHubReleaseClient(limitedHttp);
        Check((await limitedClient.CheckAsync(new Version(0, 4, 0)))?.Version == new Version(0, 5, 0), "anonymous API rate limit falls back to the public Release manifest");
        Check((await limitedClient.CheckSilentlyAsync(new Version(0, 4, 0)))?.Version == new Version(0, 5, 0), "silent startup update check returns newer release when available");
        Check(await limitedClient.CheckSilentlyAsync(new Version(0, 5, 0)) == null, "silent startup update check returns null when already up to date");
        using var offlineHttp = new HttpClient(new FailingHandler()); using var offlineClient = new GitHubReleaseClient(offlineHttp);
        Check(await offlineClient.CheckSilentlyAsync(new Version(0, 4, 0)) == null, "silent startup update check stays silent when offline or unreachable");
        using var corruptHttp = new HttpClient(new FixtureHandler("not-json"u8.ToArray())); using var corruptClient = new GitHubReleaseClient(corruptHttp);
        Check(await corruptClient.CheckSilentlyAsync(new Version(0, 4, 0)) == null, "silent startup update check stays silent on malformed response");
        var directory = Path.GetFullPath("test-results/v0.5/update-fixtures"); var path = await client.DownloadAsync(update, directory);
        Check(File.ReadAllBytes(path).SequenceEqual(payload), "downloaded installer bytes pass SHA-256 verification"); File.Delete(path);
        try { await client.DownloadAsync(update with { Sha256 = new string('0', 64) }, directory); throw new Exception("Bad hash accepted"); } catch (InvalidDataException) { Check(!Directory.EnumerateFiles(directory).Any(), "hash mismatch removes the incomplete update"); }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await client.DownloadAsync(update, directory, ct: cancel.Token); throw new Exception("Canceled download accepted"); } catch (OperationCanceledException) { Check(!Directory.EnumerateFiles(directory).Any(), "canceled update leaves no executable behind"); }
    }
    sealed class FixtureHandler(byte[] bytes) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request }); } }
    sealed class RateLimitHandler(byte[] json) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(request.RequestUri!.Host == "api.github.com" ? HttpStatusCode.Forbidden : HttpStatusCode.OK) { Content = new ByteArrayContent(json), RequestMessage = request }); }
    sealed class FailingHandler : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new HttpRequestException("Simulated network failure"); }
}
