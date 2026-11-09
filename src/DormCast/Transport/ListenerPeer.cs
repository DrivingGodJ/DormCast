using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using DormCast.Audio;
using DormCast.Hosting;
using DormCast.Networking;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using DormCast.Synchronization;

namespace DormCast.Transport;

public sealed class ListenerPeer : IAsyncDisposable
{
    readonly RTCPeerConnection _pc;
    readonly LanInterface _nic;
    readonly Channel<EncodedFrame> _frames;
    readonly Task _sender;
    readonly CancellationTokenSource _stop = new();
    readonly HostCompensation _compensation = new();
    volatile bool _connected;
    int _payloadType = 111;
    long _dropped, _sent;
    public string State { get; private set; } = "connecting";
    public bool Connected => _connected;
    public Action<object>? Signal;
    public long SentFrames => Interlocked.Read(ref _sent);
    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public double SendDelayMs => _compensation.AppliedMs;
    public double PlannedSendDelayMs => _compensation.DesiredMs;
    public void SetCompensation(double milliseconds) => _compensation.SetDesired(milliseconds);

    public ListenerPeer(LanInterface nic, IPAddress signalAddress, int frameMs)
    {
        _nic = nic;
        _frames = Channel.CreateBounded<EncodedFrame>(new BoundedChannelOptions(2100 / frameMs + 2)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true }, _ => Interlocked.Increment(ref _dropped));
        _pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = [], X_BindAddress = nic.Address,
            X_ICEIncludeAllInterfaceAddresses = false, X_UseRtpFeedbackProfile = true
        });
        // On this same-subnet-only service, the authenticated WebSocket source is the routable
        // LAN address for obfuscated browser host candidates. ICE still verifies the UDP port.
        _pc.GetRtpChannel().MdnsGetAddresses = _ => Task.FromResult(new[] { signalAddress });
        var format = new AudioFormat(111, "opus", 48000, 2,
            $"minptime={frameMs};stereo=1;sprop-stereo=1;useinbandfec=1;maxaveragebitrate=128000");
        _pc.addTrack(new MediaStreamTrack(format, MediaStreamStatusEnum.SendOnly));
        _pc.OnAudioFormatsNegotiated += formats => _payloadType = formats.First().FormatID;
        _pc.onconnectionstatechange += state =>
        {
            State = state.ToString(); _connected = state == RTCPeerConnectionState.connected;
            Signal?.Invoke(new { type = "peer-state", state = State });
        };
        _sender = Task.Run(SendLoopAsync);
    }
    public async Task AnswerAsync(string sdp)
    {
        // This broadcast accepts only one receive-only audio m-line and never accepts remote media/data channels.
        var mediaLines = sdp.Split('\n').Where(l => l.StartsWith("m=")).ToArray();
        if (mediaLines.Length != 1 || !mediaLines[0].StartsWith("m=audio ") || !sdp.Contains("a=recvonly"))
            throw new ArgumentException("仅支持单路音频接收会话。");
        var inlineCandidates = sdp.Split('\n').Where(l => l.StartsWith("a=candidate:")).Select(l => l.Trim()[2..]).ToArray();
        sdp = string.Join("\n", sdp.Split('\n').Where(l => !l.StartsWith("a=candidate:") && !l.StartsWith("a=end-of-candidates")));
        var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp });
        if (result != SetDescriptionResultEnum.OK) throw new InvalidOperationException($"音频协商失败：{result}");
        foreach (var candidate in inlineCandidates) AddCandidate(candidate, "0", 0);
        // With no STUN/TURN the one bound host candidate is gathered locally. Include it in the
        // answer, avoiding library trickle candidates whose default mid may differ from the offer.
        var deadline = Stopwatch.GetTimestamp();
        while (_pc.iceGatheringState != RTCIceGatheringState.complete && Stopwatch.GetElapsedTime(deadline).TotalSeconds < 3)
            await Task.Delay(20).ConfigureAwait(false);
        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer).ConfigureAwait(false);
        Signal?.Invoke(new { type = "answer", sdp = answer.sdp });
    }
    public void AddCandidate(string candidate, string? mid, ushort index)
    {
        if (candidate.Length > 2048) throw new ArgumentException("ICE candidate too large");
        var parsed = RTCIceCandidate.Parse(candidate);
        if (parsed.protocol != RTCIceProtocol.udp) return;
        if (IPAddress.TryParse(parsed.address, out var ip)) { if (!_nic.Contains(ip)) return; }
        else if (!parsed.address.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return;
        _pc.addIceCandidate(new RTCIceCandidateInit { candidate = candidate, sdpMid = mid, sdpMLineIndex = index });
    }
    public void Enqueue(EncodedFrame frame) { if (_connected) _frames.Writer.TryWrite(frame); }
    async Task SendLoopAsync()
    {
        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                var delay = _compensation.Advance(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
                while (_connected && Stopwatch.GetElapsedTime(frame.CapturedAt).TotalMilliseconds < delay)
                {
                    await Task.Delay(2, _stop.Token).ConfigureAwait(false);
                    delay = _compensation.Advance(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
                }
                if (!_connected || Stopwatch.GetElapsedTime(frame.CapturedAt).TotalMilliseconds - delay > 60) { Interlocked.Increment(ref _dropped); continue; }
                try { _pc.SendRtpRaw(SDPMediaTypesEnum.audio, frame.Payload, frame.RtpTimestamp, 0, _payloadType); Interlocked.Increment(ref _sent); }
                catch (Exception ex) { AppLog.Write("Peer send failed", ex); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        _connected = false; Signal = null; _frames.Writer.TryComplete(); _stop.Cancel();
        await _sender.ConfigureAwait(false); _pc.Close("broadcast ended"); _pc.Dispose(); _stop.Dispose();
    }
}
