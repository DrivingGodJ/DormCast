using System.Net.WebSockets;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DormCast.Audio;
using DormCast.Hosting;
using DormCast.Transport;

namespace DormCast.Networking;

public sealed class ClientSession(WebSocket socket, BroadcastServer server, IPAddress signalAddress)
{
    sealed record ClockPong(double ClientMs, double ReceivedMs);
    readonly Channel<object> _outgoing = Channel.CreateBounded<object>(new BoundedChannelOptions(32)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    readonly CancellationTokenSource _stop = new();
    ListenerPeer? _peer;
    JsonElement? _metrics;
    bool _background;
    double? _baseLatency;
    double? _metricsAt, _measuredAt;
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; private set; } = "浏览器";
    bool LocalPreview => server.Options.HostPlayback && signalAddress.Equals(server.Interface.Address);
    public bool Connected => _peer?.Connected == true && !LocalPreview;
    public object Snapshot() => new { id = Id, name = Name, state = LocalPreview ? "主机网页预览" : _peer?.State ?? "authenticating", localPreview = LocalPreview, background = _background, sendDelayMs = _peer?.SendDelayMs ?? 0, plannedSendDelayMs = _peer?.PlannedSendDelayMs ?? 0, baselineMs = _baseLatency, metricsAgeMs = _metricsAt is { } m ? (double?)Math.Max(0, server.NowMs - m) : null, measurementAgeMs = _measuredAt is { } t ? (double?)Math.Max(0, server.NowMs - t) : null, metrics = _metrics, sentFrames = _peer?.SentFrames ?? 0, droppedFrames = _peer?.DroppedFrames ?? 0 };
    void Send(object value) { if (!_outgoing.Writer.TryWrite(value)) { try { _stop.Cancel(); } catch (ObjectDisposedException) { } } }
    public void Enqueue(EncodedFrame frame) => _peer?.Enqueue(frame);
    public Task EndAsync()
    {
        Send(new { type = "ended", message = "主机已停止广播" });
        _outgoing.Writer.TryComplete();
        return Task.CompletedTask;
    }
    public async Task RunAsync(CancellationToken serverStop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverStop, _stop.Token);
        var ct = linked.Token;
        Task? writer = null, heartbeat = null;
        try
        {
            using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            authTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var hello = await ReadAsync(authTimeout.Token).ConfigureAwait(false);
            if (hello.GetProperty("type").GetString() != "hello" || !server.Authenticate(hello.GetProperty("token").GetString()))
            {
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "加入链接无效，请重新扫码", authTimeout.Token).ConfigureAwait(false);
                return;
            }
            Name = hello.TryGetProperty("name", out var name) ? new string((name.GetString() ?? "浏览器").Where(c => !char.IsControl(c)).Take(80).ToArray()) : "浏览器";
            writer = WriteLoopAsync(ct);
            Send(new { type = "welcome", id = Id, name = server.Options.Name, bufferMs = server.Options.BufferMs, frameMs = server.Options.FrameMs, sampleRate = 48000, serverMs = server.NowMs, syncEnabled = server.Options.SyncEnabled, serverCompensation = true, syncTargetMs = server.Synchronization.GetState(server.NowMs).TargetMs, timelineStartMs = AudioEngine.TimelineStartMs, localMonitorOnly = LocalPreview, nowPlaying = server.NowPlaying });
            _peer = new ListenerPeer(server.Interface, signalAddress, server.Options.FrameMs) { Signal = Send };
            heartbeat = HeartbeatAsync(ct);
            using var negotiate = CancellationTokenSource.CreateLinkedTokenSource(ct);
            negotiate.CancelAfter(TimeSpan.FromSeconds(12));
            var earlyCandidates = new List<JsonElement>();
            var offer = await ReadAsync(negotiate.Token).ConfigureAwait(false);
            while (offer.GetProperty("type").GetString() == "candidate" && earlyCandidates.Count < 32)
            { earlyCandidates.Add(offer.Clone()); offer = await ReadAsync(negotiate.Token).ConfigureAwait(false); }
            if (offer.GetProperty("type").GetString() != "offer") throw new ArgumentException("Expected audio offer");
            await _peer.AnswerAsync(offer.GetProperty("sdp").GetString() ?? "").ConfigureAwait(false);
            foreach (var early in earlyCandidates) Candidate(early);
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                // Protocol ping/pong remains alive when mobile browsers throttle JavaScript timers.
                var message = await ReadAsync(ct).ConfigureAwait(false);
                switch (message.GetProperty("type").GetString())
                {
                    case "candidate":
                        Candidate(message);
                        break;
                    case "ping":
                        var clientMs = message.GetProperty("clientMs").GetDouble();
                        if (!double.IsFinite(clientMs)) throw new ArgumentException("Invalid clock probe");
                        Send(new ClockPong(clientMs, server.NowMs));
                        break;
                    case "metrics":
                        _metrics = message.GetProperty("data").Clone(); _metricsAt = server.NowMs; break;
                    case "visibility":
                        _background = message.GetProperty("hidden").GetBoolean();
                        server.Synchronization.SetBackground(Id, _background); break;
                    case "sync-report":
                        if (server.Options.SyncEnabled)
                        {
                            var report = message.GetProperty("data");
                            var playing = report.GetProperty("playing").GetBoolean();
                            var natural = report.TryGetProperty("naturalMs", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDouble() : double.NaN;
                            var uncertainty = report.TryGetProperty("uncertaintyMs", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : double.NaN;
                            // Source-clock latency already contains our sender hold. Remove it before choosing the slowest base path.
                            var baseline = natural - (_peer?.SendDelayMs ?? 0);
                            if (server.Synchronization.Report(Id, $"{Name} · {Id[..4]}", Math.Max(0, baseline), uncertainty, playing && !LocalPreview, server.NowMs)) { _baseLatency = server.Synchronization.Baseline(Id); _measuredAt = server.NowMs; }
                            server.Synchronization.SetBackground(Id, _background);
                        }
                        break;
                    default: throw new ArgumentException("Unknown signalling message");
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException) { }
        catch (Exception ex) { AppLog.Write("Client session rejected/failed", ex); }
        finally
        {
            linked.Cancel(); _outgoing.Writer.TryComplete();
            if (heartbeat != null) try { await heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { }
            if (writer != null) try { await writer.ConfigureAwait(false); } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            if (_peer != null) await _peer.DisposeAsync().ConfigureAwait(false);
            socket.Abort(); socket.Dispose(); _stop.Dispose();
        }
    }
    void Candidate(JsonElement message)
    {
        var c = message.GetProperty("candidate");
        _peer!.AddCandidate(c.GetProperty("candidate").GetString() ?? "", c.TryGetProperty("sdpMid", out var mid) ? mid.GetString() : null,
            c.TryGetProperty("sdpMLineIndex", out var index) && index.ValueKind == JsonValueKind.Number ? index.GetUInt16() : (ushort)0);
    }
    async Task<JsonElement> ReadAsync(CancellationToken ct)
    {
        var bytes = new byte[32768]; var size = 0;
        while (true)
        {
            if (size == bytes.Length) throw new ArgumentException("Signal exceeds 32KiB");
            var read = await socket.ReceiveAsync(bytes.AsMemory(size), ct).ConfigureAwait(false);
            if (read.MessageType == WebSocketMessageType.Close) throw new IOException("WebSocket closed");
            if (read.MessageType != WebSocketMessageType.Text) throw new ArgumentException("Text messages only");
            size += read.Count;
            if (read.EndOfMessage) { using var doc = JsonDocument.Parse(bytes.AsMemory(0, size), new JsonDocumentOptions { MaxDepth = 12 }); return doc.RootElement.Clone(); }
        }
    }
    async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var value in _outgoing.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(3000);
                var wire = value is ClockPong pong ? new { type = "pong", clientMs = pong.ClientMs, serverReceiveMs = pong.ReceivedMs, serverSendMs = server.NowMs } : value;
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(wire), WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);
            }
            using var closeTimeout = new CancellationTokenSource(500);
            if (socket.State == WebSocketState.Open) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "broadcast ended", closeTimeout.Token).ConfigureAwait(false);
        }
        finally { _stop.Cancel(); }
    }
    async Task HeartbeatAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            var sync = server.Synchronization.GetState(server.NowMs);
            if (server.Options.SyncEnabled && server.Synchronization.Samples(Id) >= 3 && _baseLatency is { } baseline) _peer?.SetCompensation(sync.PlayoutTargetMs - baseline);
            Send(new { type = "timeline", serverMs = server.NowMs, audio = server.AudioSnapshot, nowPlaying = server.NowPlaying,
                compensation = new { senderMs = _peer?.SendDelayMs ?? 0, baselineMs = _baseLatency, background = _background, ready = server.Synchronization.Samples(Id) >= 3 },
                sync = new { enabled = server.Options.SyncEnabled, targetMs = sync.TargetMs, playoutTargetMs = sync.PlayoutTargetMs, desiredMs = sync.DesiredMs, direction = sync.Direction, referenceDevice = sync.ReferenceDevice, measuredDevices = sync.MeasuredDevices, maximumMs = sync.MaximumMs } });
        }
    }
}
