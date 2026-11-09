using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DormCast.Audio;
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
        for (var t = 0; t <= 8000; t += 50) hold.Advance(t);
        Check(Math.Abs(hold.AppliedMs - 20) < 0.1, "sender compensation decreases without browser timers");
        Check(Math.Abs(BroadcastVolumeLink.GainFromDb(-20) - 0.1) < 0.00001 && BroadcastVolumeLink.GainFromDb(float.NegativeInfinity) == 0, "broadcast gain uses Windows endpoint dB, including silence");
        Directory.CreateDirectory("test-results/v0.5"); File.WriteAllText("test-results/v0.5/stable-delay.json", JsonSerializer.Serialize(trace));
        await UpdateChecks();
    }
    static async Task UpdateChecks()
    {
        var payload = Encoding.UTF8.GetBytes("verified update fixture, never executed"); var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        string Release(string url = "https://github.com/DrivingGodJ/DormCast/releases/download/v0.5.0/DormCast-0.5.0-Setup-x64.exe", bool prerelease = false) => JsonSerializer.Serialize(new { draft = false, prerelease, tag_name = "v0.5.0", body = "Fixture", assets = new[] { new { name = "DormCast-0.5.0-Setup-x64.exe", size = payload.Length, browser_download_url = url, digest = "sha256:" + digest } } });
        var update = GitHubReleaseClient.Parse(Encoding.UTF8.GetBytes(Release()), new Version(0, 4, 0))!;
        Check(update.Version == new Version(0, 5, 0) && GitHubReleaseClient.Parse(Encoding.UTF8.GetBytes(Release()), new Version(0, 5, 0)) == null, "release comparison handles newer and current versions numerically");
        Check(GitHubReleaseClient.Parse(Encoding.UTF8.GetBytes(Release(prerelease: true)), new Version(0, 4, 0)) == null, "automatic updater ignores prereleases");
        try { GitHubReleaseClient.Parse(Encoding.UTF8.GetBytes(Release("https://example.com/bad.exe")), new Version(0, 4, 0)); throw new Exception("Foreign asset accepted"); } catch (InvalidDataException) { Console.WriteLine("PASS foreign update URL rejected"); }
        using var http = new HttpClient(new FixtureHandler(payload)); using var client = new GitHubReleaseClient(http);
        var directory = Path.GetFullPath("test-results/v0.5/update-fixtures"); var path = await client.DownloadAsync(update, directory);
        Check(File.ReadAllBytes(path).SequenceEqual(payload), "downloaded installer bytes pass SHA-256 verification"); File.Delete(path);
        try { await client.DownloadAsync(update with { Sha256 = new string('0', 64) }, directory); throw new Exception("Bad hash accepted"); } catch (InvalidDataException) { Check(!Directory.EnumerateFiles(directory).Any(), "hash mismatch removes the incomplete update"); }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await client.DownloadAsync(update, directory, ct: cancel.Token); throw new Exception("Canceled download accepted"); } catch (OperationCanceledException) { Check(!Directory.EnumerateFiles(directory).Any(), "canceled update leaves no executable behind"); }
    }
    sealed class FixtureHandler(byte[] bytes) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request }); } }
}
