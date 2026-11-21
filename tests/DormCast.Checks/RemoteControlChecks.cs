using System.Net.WebSockets;
using System.Text.Json;
using DormCast.Hosting;
using DormCast.Networking;

static class RemoteControlChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }

    public static async Task RunAsync()
    {
        var legacyJson = """{"Name":"宿舍之音","Port":8080,"FrameMs":20,"BufferMs":60,"SyncEnabled":true,"HostPlayback":false}""";
        var migrated = JsonSerializer.Deserialize<BroadcastOptions>(legacyJson)!;
        Check(migrated.RemoteControlEnabled, "legacy settings.json defaults RemoteControlEnabled to true");

        if (IntPtr.Size == 8)
            Check(NowPlayingService.InputStructSize == 40, "Win64 SendInput INPUT struct size is 40 bytes");

        await using (var disabledService = new HostControlService(new BroadcastOptions(RemoteControlEnabled: false)))
        {
            var r = await disabledService.ExecuteAsync("play", null, null, null);
            Check(!r.Ok && r.State?.Enabled == false, "HostControlService rejects commands when RemoteControlEnabled is false");
        }
        await using (var validatorService = new HostControlService(new BroadcastOptions(RemoteControlEnabled: true)))
        {
            var badAction = await validatorService.ExecuteAsync("explode", null, null, null);
            var badVolumeHigh = await validatorService.ExecuteAsync("volume", 1.5f, null, null);
            var badVolumeNan = await validatorService.ExecuteAsync("volume", float.NaN, null, null);
            var badStep = await validatorService.ExecuteAsync("volume-step", 2.0f, null, null);
            Check(!badAction.Ok && !badVolumeHigh.Ok && !badVolumeNan.Ok && !badStep.Ok, "HostControlService rejects unknown actions and invalid volume bounds");
        }

        var nic = LanInterface.Discover().FirstOrDefault() ?? throw new Exception("No LAN interface for remote control checks");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var mock = new MockHostController(enabled: true);
        await using (var host = new BroadcastHost())
        {
            await host.StartAsync(new BroadcastOptions(Port: 18096, RemoteControlEnabled: true), nic, mock);
            var ipv4 = new Uri(host.IpJoinUrl!);
            var token = ipv4.Fragment[6..];

            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            var html = await http.GetStringAsync(new Uri(ipv4, "/"), deadline.Token);
            var js = await http.GetStringAsync(new Uri(ipv4, "/app.js"), deadline.Token);
            Check(html.Contains("id=\"hostPrev\"") && html.Contains("id=\"hostPlayPause\"") && html.Contains("id=\"hostNext\"") && html.Contains("id=\"hostVolume\"") && html.Contains("id=\"hostMute\""),
                "embedded browser UI exposes previous, play/pause, next, mute and volume controls");
            Check(js.Contains("'host-state'") && js.Contains("'control-result'"),
                "embedded browser script handles remote control replies and real-time host state broadcasts");

            using var c1 = await ConnectAsync(ipv4, token, "remote-1", deadline.Token);
            using var c2 = await ConnectAsync(ipv4, token, "remote-2", deadline.Token);

            Check(c1.Welcome.GetProperty("hostControl").GetProperty("Enabled").GetBoolean(), "welcome advertises remote control enabled");
            Check(Math.Abs(c1.Welcome.GetProperty("hostControl").GetProperty("Volume").GetProperty("Level").GetSingle() - 0.5f) < 0.001f, "welcome includes initial host volume");

            // Play
            await SendJsonAsync(c1.Socket, new { type = "control", action = "play" }, deadline.Token);
            var playResult = await ReadUntilTypeAsync(c1.Socket, "control-result", deadline.Token);
            var playPush = await ReadUntilTypeAsync(c2.Socket, "host-state", deadline.Token);
            Check(playResult.GetProperty("ok").GetBoolean() && playResult.GetProperty("nowPlaying").GetProperty("Playing").GetBoolean(), "browser play command starts host playback");
            Check(playPush.GetProperty("nowPlaying").GetProperty("Playing").GetBoolean(), "peer browser receives immediate play state broadcast");

            // Pause
            await SendJsonAsync(c1.Socket, new { type = "control", action = "pause" }, deadline.Token);
            var pauseResult = await ReadUntilTypeAsync(c1.Socket, "control-result", deadline.Token);
            var pausePush = await ReadUntilTypeAsync(c2.Socket, "host-state", deadline.Token);
            Check(pauseResult.GetProperty("ok").GetBoolean() && !pauseResult.GetProperty("nowPlaying").GetProperty("Playing").GetBoolean(), "browser pause command pauses host playback");
            Check(!pausePush.GetProperty("nowPlaying").GetProperty("Playing").GetBoolean(), "peer browser receives immediate pause state broadcast");

            // Next track
            await SendJsonAsync(c1.Socket, new { type = "control", action = "next" }, deadline.Token);
            var nextResult = await ReadUntilTypeAsync(c1.Socket, "control-result", deadline.Token);
            var nextPush = await ReadUntilTypeAsync(c2.Socket, "host-state", deadline.Token);
            Check(nextResult.GetProperty("ok").GetBoolean() && nextResult.GetProperty("nowPlaying").GetProperty("Title").GetString() == "Track 2", "browser next command skips to next track");
            Check(nextPush.GetProperty("nowPlaying").GetProperty("Title").GetString() == "Track 2", "peer browser receives next track broadcast");

            // Previous track
            await SendJsonAsync(c1.Socket, new { type = "control", action = "previous" }, deadline.Token);
            var prevResult = await ReadUntilTypeAsync(c1.Socket, "control-result", deadline.Token);
            var prevPush = await ReadUntilTypeAsync(c2.Socket, "host-state", deadline.Token);
            Check(prevResult.GetProperty("ok").GetBoolean() && prevResult.GetProperty("nowPlaying").GetProperty("Title").GetString() == "Track 1", "browser previous command returns to previous track");
            Check(prevPush.GetProperty("nowPlaying").GetProperty("Title").GetString() == "Track 1", "peer browser receives previous track broadcast");

            // Volume set
            await SendJsonAsync(c1.Socket, new { type = "control", action = "volume", level = 0.72 }, deadline.Token);
            var volResult = await ReadUntilTypeAsync(c1.Socket, "control-result", deadline.Token);
            var volPush = await ReadUntilTypeAsync(c2.Socket, "host-state", deadline.Token);
            Check(volResult.GetProperty("ok").GetBoolean() && Math.Abs(volResult.GetProperty("hostControl").GetProperty("Volume").GetProperty("Level").GetSingle() - 0.72f) < 0.001f, "browser volume command sets host volume");
            Check(Math.Abs(volPush.GetProperty("hostControl").GetProperty("Volume").GetProperty("Level").GetSingle() - 0.72f) < 0.001f, "peer browser receives host volume broadcast");

            // Volume step
            await SendJsonAsync(c1.Socket, new { type = "control", action = "volume-step", delta = -0.10 }, deadline.Token);
            var stepResult = await ReadUntilTypeAsync(c1.Socket, "control-result", deadline.Token);
            Check(stepResult.GetProperty("ok").GetBoolean() && Math.Abs(stepResult.GetProperty("hostControl").GetProperty("Volume").GetProperty("Level").GetSingle() - 0.62f) < 0.005f, "browser volume-step adjusts host volume incrementally");

            // Mute and toggle
            await SendJsonAsync(c1.Socket, new { type = "control", action = "mute", muted = true }, deadline.Token);
            var muteResult = await ReadUntilTypeAsync(c1.Socket, "control-result", deadline.Token);
            Check(muteResult.GetProperty("ok").GetBoolean() && muteResult.GetProperty("hostControl").GetProperty("Volume").GetProperty("Muted").GetBoolean(), "browser mute command mutes host speaker");

            await SendJsonAsync(c1.Socket, new { type = "control", action = "mute-toggle" }, deadline.Token);
            var unmuteResult = await ReadUntilTypeAsync(c1.Socket, "control-result", deadline.Token);
            Check(unmuteResult.GetProperty("ok").GetBoolean() && !unmuteResult.GetProperty("hostControl").GetProperty("Volume").GetProperty("Muted").GetBoolean(), "browser mute-toggle unmutes host speaker");

            // Invalid control payload does not kill session
            await SendJsonAsync(c1.Socket, new { type = "control", action = "volume", level = 5.0 }, deadline.Token);
            var badResult = await ReadUntilTypeAsync(c1.Socket, "control-result", deadline.Token);
            Check(!badResult.GetProperty("ok").GetBoolean() && c1.Socket.State == WebSocketState.Open, "invalid remote control value returns ok=false without dropping the session");

            await c1.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
            await c2.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
            await host.StopAsync();
        }

        var disabledMock = new MockHostController(enabled: false);
        await using (var host = new BroadcastHost())
        {
            await host.StartAsync(new BroadcastOptions(Port: 18097, RemoteControlEnabled: false), nic, disabledMock);
            var ipv4 = new Uri(host.IpJoinUrl!);
            using var c = await ConnectAsync(ipv4, ipv4.Fragment[6..], "remote-disabled", deadline.Token);
            Check(!c.Welcome.GetProperty("hostControl").GetProperty("Enabled").GetBoolean(), "welcome advertises remote control disabled when turned off");
            await SendJsonAsync(c.Socket, new { type = "control", action = "next" }, deadline.Token);
            var rejected = await ReadUntilTypeAsync(c.Socket, "control-result", deadline.Token);
            Check(!rejected.GetProperty("ok").GetBoolean(), "control command is rejected when RemoteControlEnabled is false");
            await c.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
            await host.StopAsync();
        }
    }

    sealed record ConnectedClient(ClientWebSocket Socket, JsonElement Welcome) : IDisposable
    {
        public void Dispose() => Socket.Dispose();
    }

    static async Task<ConnectedClient> ConnectAsync(Uri ipv4, string token, string name, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader("Origin", $"http://{ipv4.Authority}");
            await socket.ConnectAsync(new Uri($"ws://{ipv4.Authority}/signal"), ct);
            await SendJsonAsync(socket, new { type = "hello", token, name }, ct);
            var welcome = await ReadUntilTypeAsync(socket, "welcome", ct);
            return new ConnectedClient(socket, welcome);
        }
        catch { socket.Dispose(); throw; }
    }

    static Task SendJsonAsync(ClientWebSocket socket, object payload, CancellationToken ct) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, ct);

    static async Task<JsonElement> ReadUntilTypeAsync(ClientWebSocket socket, string expectedType, CancellationToken ct)
    {
        var buffer = new byte[32768];
        for (var i = 0; i < 20; i++)
        {
            var count = 0;
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), ct);
                if (result.MessageType == WebSocketMessageType.Close) throw new IOException("WebSocket closed unexpectedly");
                count += result.Count;
            } while (!result.EndOfMessage);
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, count));
            var root = doc.RootElement.Clone();
            if (root.TryGetProperty("type", out var t) && t.GetString() == expectedType) return root;
        }
        throw new TimeoutException($"Timed out waiting for WebSocket message type '{expectedType}'");
    }

    sealed class MockHostController(bool enabled) : IHostController
    {
        readonly string[] _tracks = ["Track 1", "Track 2", "Track 3"];
        int _index;
        bool _playing;
        float _level = 0.5f;
        bool _muted;
        public event Action? StateChanged;
        public NowPlayingInfo NowPlaying => new(_tracks[_index], "Mock Artist", "Mock Album", "mock.app", _playing, null, 12, 200, true, true, true, true, true);
        public HostControlState ControlState => new(enabled, new HostVolumeState(enabled, _level, _muted, enabled ? "Mock Speaker" : null));
        public CoverImage? Cover(string revision) => null;
        public void Start() { }
        public Task<HostControlResult> ExecuteAsync(string action, float? level, float? delta, bool? muted, CancellationToken ct = default)
        {
            if (!enabled) return Task.FromResult(new HostControlResult(false, action, "主机未开启浏览器远程控制", ControlState, NowPlaying));
            switch (action)
            {
                case "play": _playing = true; break;
                case "pause": _playing = false; break;
                case "toggle": _playing = !_playing; break;
                case "next": _index = (_index + 1) % _tracks.Length; break;
                case "previous": _index = (_index - 1 + _tracks.Length) % _tracks.Length; break;
                case "volume":
                    if (level is not { } v || !float.IsFinite(v) || v < 0 || v > 1)
                        return Task.FromResult(new HostControlResult(false, action, "无效音量", ControlState, NowPlaying));
                    _level = v; if (v > 0) _muted = false; break;
                case "volume-step":
                    if (delta is not { } d || !float.IsFinite(d) || d < -1 || d > 1)
                        return Task.FromResult(new HostControlResult(false, action, "无效步进", ControlState, NowPlaying));
                    _level = Math.Clamp(_level + d, 0f, 1f); if (_level > 0) _muted = false; break;
                case "mute": _muted = muted ?? !_muted; break;
                case "mute-toggle": _muted = !_muted; break;
                default: return Task.FromResult(new HostControlResult(false, action, "未知操作", ControlState, NowPlaying));
            }
            return Task.FromResult(new HostControlResult(true, action, "ok", ControlState, NowPlaying));
        }
        public ValueTask DisposeAsync()
        {
            _ = StateChanged;
            return ValueTask.CompletedTask;
        }
    }
}
