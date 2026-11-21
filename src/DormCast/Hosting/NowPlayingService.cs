using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DormCast.Hosting;

public sealed record NowPlayingInfo(
    string Title,
    string Artist,
    string Album,
    string Source,
    bool Playing,
    string? CoverRevision,
    double PositionSeconds,
    double DurationSeconds,
    bool HasSession = false,
    bool CanPlay = false,
    bool CanPause = false,
    bool CanPrevious = false,
    bool CanNext = false)
{
    public static NowPlayingInfo Empty { get; } = new("", "", "", "", false, null, 0, 0);
}
public sealed record CoverImage(string Revision, string ContentType, byte[] Bytes);

public sealed class NowPlayingService : IAsyncDisposable
{
    const ushort VkMediaNextTrack = 0xB0;
    const ushort VkMediaPrevTrack = 0xB1;
    const ushort VkMediaPlayPause = 0xB3;
    readonly CancellationTokenSource _stop = new();
    readonly SemaphoreSlim _refresh = new(0, 1);
    readonly SemaphoreSlim _controlLock = new(1, 1);
    Task? _worker;
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    string? _lastSourceId;
    NowPlayingInfo _current = NowPlayingInfo.Empty;
    CoverImage? _cover;
    public event Action<NowPlayingInfo>? Changed;
    public NowPlayingInfo Current => Volatile.Read(ref _current);
    public CoverImage? Cover(string revision) { var cover = Volatile.Read(ref _cover); return cover?.Revision == revision ? cover : null; }
    public void Start() => _worker = Task.Run(PollAsync);
    public void TriggerRefresh()
    {
        try { if (_refresh.CurrentCount == 0) _refresh.Release(); }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }
    static string Text(string? text) => new((text ?? "").Where(c => !char.IsControl(c)).Take(240).ToArray());
    GlobalSystemMediaTransportControlsSession? SelectSession(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var session = manager.GetCurrentSession();
        if (session?.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            return session;
        var sessions = manager.GetSessions();
        var playing = sessions.FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        if (playing != null) return playing;
        if (session != null) return session;
        var lastSource = Volatile.Read(ref _lastSourceId);
        if (!string.IsNullOrEmpty(lastSource))
        {
            var matched = sessions.FirstOrDefault(s => string.Equals(s.SourceAppUserModelId, lastSource, StringComparison.OrdinalIgnoreCase));
            if (matched != null) return matched;
        }
        return sessions.FirstOrDefault();
    }
    async Task<GlobalSystemMediaTransportControlsSessionManager?> GetManagerAsync(CancellationToken ct)
    {
        var manager = Volatile.Read(ref _manager);
        if (manager != null) return manager;
        manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct).WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        Volatile.Write(ref _manager, manager);
        return manager;
    }
    public async Task<HostControlResult> ControlAsync(string action, CancellationToken ct = default)
    {
        if (action is not ("play" or "pause" or "toggle" or "previous" or "next"))
            return new(false, action, "不支持的播放控制指令");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, ct);
        var token = linked.Token;
        if (!await _controlLock.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false))
            return new(false, action, "主机正在处理上一条控制指令");
        try
        {
            GlobalSystemMediaTransportControlsSession? session = null;
            try
            {
                var manager = await GetManagerAsync(token).ConfigureAwait(false);
                if (manager != null) session = SelectSession(manager);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { AppLog.Write("Resolve Windows media session for control", ex); }

            if (session != null)
            {
                Volatile.Write(ref _lastSourceId, session.SourceAppUserModelId);
                var playback = session.GetPlaybackInfo();
                var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var controls = playback.Controls;
                bool ok = false;
                try
                {
                    switch (action)
                    {
                        case "play":
                            if (playing) { ScheduleFollowUpRefresh(); return new(true, action, "主机已在播放"); }
                            ok = await session.TryPlayAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                            if (!ok && controls.IsPlayPauseToggleEnabled)
                                ok = await session.TryTogglePlayPauseAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                            break;
                        case "pause":
                            if (!playing) { ScheduleFollowUpRefresh(); return new(true, action, "主机已暂停"); }
                            ok = await session.TryPauseAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                            if (!ok && controls.IsPlayPauseToggleEnabled)
                                ok = await session.TryTogglePlayPauseAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                            break;
                        case "toggle":
                            ok = await session.TryTogglePlayPauseAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                            if (!ok)
                                ok = playing
                                    ? await session.TryPauseAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false)
                                    : await session.TryPlayAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                            break;
                        case "previous":
                            ok = await session.TrySkipPreviousAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                            break;
                        case "next":
                            ok = await session.TrySkipNextAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                            break;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { AppLog.Write($"Windows media control ({action})", ex); }

                if (ok)
                {
                    ScheduleFollowUpRefresh();
                    return new(true, action);
                }
                // Only fall back to global media key when play/pause direction matches current state.
                var canFallbackKey = action switch
                {
                    "play" => !playing,
                    "pause" => playing,
                    _ => true
                };
                if (canFallbackKey && SendMediaKey(action))
                {
                    ScheduleFollowUpRefresh();
                    return new(true, action);
                }
                return new(false, action, "当前播放器不支持此操作");
            }

            if (SendMediaKey(action))
            {
                ScheduleFollowUpRefresh();
                return new(true, action, "已发送系统媒体键");
            }
            return new(false, action, "主机当前没有可控制的播放器");
        }
        finally
        {
            _controlLock.Release();
        }
    }
    void ScheduleFollowUpRefresh()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150, _stop.Token).ConfigureAwait(false);
                TriggerRefresh();
                await Task.Delay(550, _stop.Token).ConfigureAwait(false);
                TriggerRefresh();
            }
            catch (OperationCanceledException) { }
        });
    }
    async Task PollAsync()
    {
        try
        {
            var manager = await GetManagerAsync(_stop.Token).ConfigureAwait(false);
            if (manager == null) return;
            var lastCoverCheck = DateTimeOffset.MinValue;
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var session = SelectSession(manager);
                    var previous = Current;
                    if (session == null)
                    {
                        Volatile.Write(ref _cover, null);
                        Volatile.Write(ref _current, NowPlayingInfo.Empty);
                        if (HasVisibleChange(previous, NowPlayingInfo.Empty)) Changed?.Invoke(NowPlayingInfo.Empty);
                    }
                    else
                    {
                        Volatile.Write(ref _lastSourceId, session.SourceAppUserModelId);
                        var properties = await session.TryGetMediaPropertiesAsync().AsTask(_stop.Token).WaitAsync(TimeSpan.FromSeconds(3), _stop.Token);
                        var title = Text(properties.Title); var artist = Text(properties.Artist); var album = Text(properties.AlbumTitle); var source = Text(session.SourceAppUserModelId);
                        var changed = title != previous.Title || artist != previous.Artist || album != previous.Album || source != previous.Source;
                        var cover = changed ? null : Volatile.Read(ref _cover);
                        if (changed || cover == null || DateTimeOffset.UtcNow - lastCoverCheck > TimeSpan.FromSeconds(30))
                        {
                            cover = await ReadCoverAsync(properties.Thumbnail); lastCoverCheck = DateTimeOffset.UtcNow;
                        }
                        var playback = session.GetPlaybackInfo();
                        var controls = playback.Controls;
                        var timeline = session.GetTimelineProperties();
                        var next = new NowPlayingInfo(
                            title, artist, album, source,
                            playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                            cover?.Revision, Math.Max(0, timeline.Position.TotalSeconds), Math.Max(0, timeline.EndTime.TotalSeconds),
                            HasSession: true,
                            CanPlay: controls.IsPlayEnabled || controls.IsPlayPauseToggleEnabled,
                            CanPause: controls.IsPauseEnabled || controls.IsPlayPauseToggleEnabled,
                            CanPrevious: controls.IsPreviousEnabled,
                            CanNext: controls.IsNextEnabled);
                        Volatile.Write(ref _cover, cover);
                        Volatile.Write(ref _current, next);
                        if (HasVisibleChange(previous, next)) Changed?.Invoke(next);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) { AppLog.Write("Read Windows media metadata", ex); }
                try { await _refresh.WaitAsync(2000, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { AppLog.Write("Windows media session unavailable", ex); }
    }
    static bool HasVisibleChange(NowPlayingInfo a, NowPlayingInfo b) =>
        a.Title != b.Title || a.Artist != b.Artist || a.Album != b.Album || a.Source != b.Source ||
        a.Playing != b.Playing || a.CoverRevision != b.CoverRevision || a.HasSession != b.HasSession ||
        a.CanPlay != b.CanPlay || a.CanPause != b.CanPause || a.CanPrevious != b.CanPrevious || a.CanNext != b.CanNext;
    async Task<CoverImage?> ReadCoverAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail == null) return null;
        try
        {
            using var stream = await thumbnail.OpenReadAsync().AsTask(_stop.Token).WaitAsync(TimeSpan.FromSeconds(3), _stop.Token);
            if (stream.Size is 0 or > 2 * 1024 * 1024) return null;
            using var reader = new DataReader(stream);
            var length = await reader.LoadAsync((uint)stream.Size).AsTask(_stop.Token).WaitAsync(TimeSpan.FromSeconds(3), _stop.Token);
            var bytes = new byte[length]; reader.ReadBytes(bytes);
            var mime = ImageType(bytes); if (mime == null) return null;
            return new CoverImage(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16], mime, bytes);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return null; }
        catch (Exception ex) { AppLog.Write("Read Windows media cover", ex); return null; }
    }
    public static string? ImageType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) return "image/jpeg";
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        return null;
    }
    static bool SendMediaKey(string action)
    {
        var vk = action switch
        {
            "play" or "pause" or "toggle" => VkMediaPlayPause,
            "previous" => VkMediaPrevTrack,
            "next" => VkMediaNextTrack,
            _ => (ushort)0
        };
        if (vk == 0) return false;
        var inputs = new INPUT[]
        {
            new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = 0x0001 } } },
            new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = 0x0001 | 0x0002 } } }
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }
    public static int InputStructSize => Marshal.SizeOf<INPUT>();
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        TriggerRefresh();
        if (_worker != null) await _worker.ConfigureAwait(false);
        _refresh.Dispose();
        _controlLock.Dispose();
        _stop.Dispose();
    }
}
