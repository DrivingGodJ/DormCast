using System.Text.Json;
using DormCast.Audio;
using DormCast.Networking;
using DormCast.Synchronization;

namespace DormCast.Hosting;

public sealed class BroadcastHost : IAsyncDisposable
{
    readonly SemaphoreSlim _lifecycle = new(1);
    AudioEngine? _audio;
    BroadcastServer? _server;
    HostMonitor? _monitor;
    DefaultAudioRoute? _route;
    BroadcastVolumeLink? _volumeLink;
    public bool Running => _server != null;
    public int ConnectedCount => _server?.ConnectedCount ?? 0;
    public string? JoinUrl => _server?.JoinUrl;
    public string? IpJoinUrl => _server?.IpJoinUrl;
    public string AddressStatus => _server?.AddressStatus ?? "尚未广播";
    public NowPlayingInfo NowPlaying => _server?.NowPlaying ?? NowPlayingInfo.Empty;
    public HostControlState? HostControl => _server?.HostControl;
    public string DeviceName => _audio?.DeviceName ?? "开始广播后连接默认输出设备";
    public string CaptureStatus => _audio?.CaptureStatus ?? "尚未采集";
    public double Peak => _audio?.Peak ?? 0;
    public bool NetworkAvailable => _server?.Interface.IsAvailable() ?? true;
    public Exception? AudioFailure => _audio?.Failure;
    public object[] Clients => _server?.Clients ?? [];
    public SyncState? SyncState => _server?.Synchronization.GetState(_audio!.NowMs);
    public string MonitorStatus => _monitor?.Status ?? "主机未启用延迟播放";
    public async Task StartAsync(BroadcastOptions options, LanInterface nic, IHostController? controller = null)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Running) return;
            if (options.HostPlayback)
            {
                var devices = AudioDevices.RenderDevices();
                var autoVirtual = AudioDevices.FindVirtualCaptureDevice(devices);
                if (autoVirtual == null) throw new InvalidOperationException("未检测到虚拟声卡（VB-CABLE），无法启动广播。请先安装虚拟声卡后再重试。");
                var captureId = !string.IsNullOrWhiteSpace(options.CaptureDeviceId) && devices.Any(d => d.Id == options.CaptureDeviceId && d.IsVirtual)
                    ? options.CaptureDeviceId
                    : autoVirtual.Id;
                options = options with { SyncEnabled = true, CaptureDeviceId = captureId };
            }
            options.Validate();
            if (!nic.IsAvailable()) throw new IOException("所选局域网地址已不可用，请刷新网卡列表。");
            if (options.HostPlayback) AudioDevices.ValidateRoute(options.CaptureDeviceId, options.OutputDeviceId);
            var audio = new AudioEngine(options.FrameMs, options.CaptureDeviceId) { EncodeLocalMonitor = options.HostPlayback };
            var server = new BroadcastServer(options, nic, audio, controller);
            HostMonitor? monitor = null;
            DefaultAudioRoute? route = null;
            BroadcastVolumeLink? volumeLink = null;
            try
            {
                audio.Start(); await server.StartAsync().ConfigureAwait(false);
                if (options.HostPlayback)
                {
                    monitor = new HostMonitor(audio, options.OutputDeviceId!, server.Synchronization, options.HostHardwareCorrectionMs); monitor.Start();
                    route = DefaultAudioRoute.SwitchTo(options.CaptureDeviceId!, options.OutputDeviceId!);
                    volumeLink = new BroadcastVolumeLink(audio, options.CaptureDeviceId!, options.OutputDeviceId!); volumeLink.Start();
                }
                _audio = audio; _server = server; _monitor = monitor; _route = route; _volumeLink = volumeLink;
                AppLog.Write($"Broadcast started on {nic.Address}:{options.Port}");
            }
            catch
            {
                try { volumeLink?.Dispose(); monitor?.Dispose(); } finally { try { audio.Dispose(); } finally { route?.Dispose(); await server.DisposeAsync().ConfigureAwait(false); } } throw;
            }
        }
        finally { _lifecycle.Release(); }
    }
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var audio = _audio; var server = _server; var monitor = _monitor; var route = _route; var volume = _volumeLink; _audio = null; _server = null; _monitor = null; _route = null; _volumeLink = null;
            try { try { volume?.Dispose(); monitor?.Dispose(); } finally { try { audio?.Dispose(); } finally { route?.Dispose(); } } }
            finally { if (server != null) await server.DisposeAsync().ConfigureAwait(false); }
            if (server != null) AppLog.Write("Broadcast stopped; capture, peers and listener released");
        }
        finally { _lifecycle.Release(); }
    }
    public static Version AppVersion { get; } = NormalizeVersion(typeof(BroadcastHost).Assembly.GetName().Version);
    static Version NormalizeVersion(Version? v) => v == null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    public string Diagnostics() => JsonSerializer.Serialize(new
    {
        version = AppVersion.ToString(), utc = DateTimeOffset.UtcNow, running = Running,
        endpoint = _server?.BaseUrl, fixedHost = JoinIdentity.Persistent.HostName, addressStatus = AddressStatus, nowPlaying = NowPlaying, hostControl = HostControl,
        audio = _audio?.Snapshot(), clients = Clients, synchronization = SyncState, hostPlayback = _monitor?.Snapshot(),
        latencyNote = "RTT / jitter / jitterBuffer are not measured acoustic end-to-end latency."
    }, new JsonSerializerOptions { WriteIndented = true });
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); _lifecycle.Dispose(); }
}
