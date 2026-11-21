using System.Diagnostics;
using DormCast.Audio;

namespace DormCast.Hosting;

public sealed record HostVolumeState(bool Available, float Level, bool Muted, string? DeviceName);

public sealed record HostControlState(bool Enabled, HostVolumeState Volume);

public sealed record HostControlResult(bool Ok, string Action, string? Message = null, HostControlState? State = null, NowPlayingInfo? NowPlaying = null);

public interface IHostController : IAsyncDisposable
{
    NowPlayingInfo NowPlaying { get; }
    HostControlState ControlState { get; }
    CoverImage? Cover(string revision);
    event Action? StateChanged;
    void Start();
    Task<HostControlResult> ExecuteAsync(string action, float? level, float? delta, bool? muted, CancellationToken ct = default);
}

public sealed class HostControlService(BroadcastOptions options) : IHostController
{
    readonly NowPlayingService _music = new();
    readonly object _volumeGate = new();
    SpeakerVolumeController? _volume;
    long _lastTransportTicks;
    string? _lastTransportAction;
    float _desiredLevel;
    long _desiredLevelAt;
    bool _desiredMuted;
    long _desiredMutedAt;
    public event Action? StateChanged;
    public NowPlayingInfo NowPlaying => _music.Current;
    public HostControlState ControlState => new(options.RemoteControlEnabled, ReadVolume());
    public CoverImage? Cover(string revision) => _music.Cover(revision);
    public void Start()
    {
        _music.Changed += OnMusicChanged;
        _music.Start();
        if (options.RemoteControlEnabled)
        {
            _volume = new SpeakerVolumeController(options.OutputDeviceId);
            _volume.Changed += OnVolumeChanged;
        }
    }
    void OnMusicChanged(NowPlayingInfo _) => StateChanged?.Invoke();
    void OnVolumeChanged(SpeakerVolumeState _) => StateChanged?.Invoke();
    HostVolumeState ReadVolume()
    {
        if (!options.RemoteControlEnabled) return new(false, 0, false, null);
        var current = _volume?.Current;
        if (current == null) return new(false, 0, false, null);
        lock (_volumeGate)
        {
            var level = _desiredLevelAt != 0 && Stopwatch.GetElapsedTime(_desiredLevelAt).TotalMilliseconds < 200 ? _desiredLevel : current.Level;
            var muted = _desiredMutedAt != 0 && Stopwatch.GetElapsedTime(_desiredMutedAt).TotalMilliseconds < 200 ? _desiredMuted : current.Muted;
            return new(true, Math.Clamp(level, 0f, 1f), muted, current.Name);
        }
    }
    public async Task<HostControlResult> ExecuteAsync(string action, float? level, float? delta, bool? muted, CancellationToken ct = default)
    {
        var normalized = (action ?? "").Trim().ToLowerInvariant();
        if (!options.RemoteControlEnabled)
            return new(false, normalized, "主机未开启浏览器远程控制", ControlState, NowPlaying);

        switch (normalized)
        {
            case "play":
            case "pause":
            case "toggle":
            case "previous":
            case "next":
            {
                var now = Stopwatch.GetTimestamp();
                var previousTicks = Interlocked.Read(ref _lastTransportTicks);
                if (previousTicks != 0 && _lastTransportAction == normalized && Stopwatch.GetElapsedTime(previousTicks).TotalMilliseconds < 120)
                    return new(true, normalized, null, ControlState, NowPlaying);
                Interlocked.Exchange(ref _lastTransportTicks, now);
                _lastTransportAction = normalized;
                var result = await _music.ControlAsync(normalized, ct).ConfigureAwait(false);
                var feedback = result.Message ?? (result.Ok ? normalized switch
                {
                    "play" => "主机已开始播放",
                    "pause" => "主机已暂停",
                    "toggle" => "已切换主机播放状态",
                    "previous" => "已切到上一首",
                    "next" => "已切到下一首",
                    _ => null
                } : null);
                return result with { Message = feedback, State = ControlState, NowPlaying = NowPlaying };
            }
            case "volume":
            {
                if (level is not { } raw || !float.IsFinite(raw) || raw < -0.001f || raw > 1.001f)
                    return new(false, normalized, "音量数值须在 0%–100% 之间", ControlState, NowPlaying);
                var current = _volume?.Current;
                if (_volume == null || current == null)
                    return new(false, normalized, "主机当前没有可用的扬声器设备", ControlState, NowPlaying);
                var clamped = Math.Clamp(raw, 0f, 1f);
                var nextMuted = clamped > 0 ? false : current.Muted;
                lock (_volumeGate)
                {
                    _desiredLevel = clamped; _desiredLevelAt = Stopwatch.GetTimestamp();
                    if (clamped > 0) { _desiredMuted = false; _desiredMutedAt = _desiredLevelAt; }
                }
                _volume.RequestLevel(clamped);
                var nextState = new HostControlState(true, new HostVolumeState(true, clamped, nextMuted, current.Name));
                return new(true, normalized, null, nextState, NowPlaying);
            }
            case "volume-step":
            {
                if (delta is not { } step || !float.IsFinite(step) || step is < -1f or > 1f)
                    return new(false, normalized, "音量步进数值无效", ControlState, NowPlaying);
                var current = _volume?.Current;
                if (_volume == null || current == null)
                    return new(false, normalized, "主机当前没有可用的扬声器设备", ControlState, NowPlaying);
                float nextLevel;
                bool nextMuted;
                lock (_volumeGate)
                {
                    var baseLevel = _desiredLevelAt != 0 && Stopwatch.GetElapsedTime(_desiredLevelAt).TotalMilliseconds < 200 ? _desiredLevel : current.Level;
                    nextLevel = Math.Clamp(MathF.Round((baseLevel + step) * 100f) / 100f, 0f, 1f);
                    nextMuted = nextLevel > 0 ? false : current.Muted;
                    _desiredLevel = nextLevel; _desiredLevelAt = Stopwatch.GetTimestamp();
                    if (nextLevel > 0) { _desiredMuted = false; _desiredMutedAt = _desiredLevelAt; }
                }
                _volume.RequestLevel(nextLevel);
                var nextState = new HostControlState(true, new HostVolumeState(true, nextLevel, nextMuted, current.Name));
                return new(true, normalized, null, nextState, NowPlaying);
            }
            case "mute":
            case "mute-toggle":
            {
                var current = _volume?.Current;
                if (_volume == null || current == null)
                    return new(false, normalized, "主机当前没有可用的扬声器设备", ControlState, NowPlaying);
                bool nextMuted;
                float effectiveLevel;
                lock (_volumeGate)
                {
                    var currentMuted = _desiredMutedAt != 0 && Stopwatch.GetElapsedTime(_desiredMutedAt).TotalMilliseconds < 200 ? _desiredMuted : current.Muted;
                    nextMuted = normalized == "mute-toggle" ? !currentMuted : muted ?? !currentMuted;
                    effectiveLevel = _desiredLevelAt != 0 && Stopwatch.GetElapsedTime(_desiredLevelAt).TotalMilliseconds < 200 ? _desiredLevel : current.Level;
                    _desiredMuted = nextMuted; _desiredMutedAt = Stopwatch.GetTimestamp();
                }
                _volume.RequestMute(nextMuted);
                var nextState = new HostControlState(true, new HostVolumeState(true, effectiveLevel, nextMuted, current.Name));
                return new(true, normalized, nextMuted ? "主机已静音" : "主机已取消静音", nextState, NowPlaying);
            }
            default:
                return new(false, normalized, "不支持的控制指令", ControlState, NowPlaying);
        }
    }
    public async ValueTask DisposeAsync()
    {
        _music.Changed -= OnMusicChanged;
        if (_volume != null)
        {
            _volume.Changed -= OnVolumeChanged;
            _volume.Dispose();
            _volume = null;
        }
        await _music.DisposeAsync().ConfigureAwait(false);
    }
}
