using System.Security.Cryptography;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DormCast.Hosting;

public sealed record NowPlayingInfo(string Title, string Artist, string Album, string Source, bool Playing, string? CoverRevision, double PositionSeconds, double DurationSeconds)
{
    public static NowPlayingInfo Empty { get; } = new("", "", "", "", false, null, 0, 0);
}
public sealed record CoverImage(string Revision, string ContentType, byte[] Bytes);

public sealed class NowPlayingService : IAsyncDisposable
{
    readonly CancellationTokenSource _stop = new();
    Task? _worker;
    NowPlayingInfo _current = NowPlayingInfo.Empty;
    CoverImage? _cover;
    public NowPlayingInfo Current => Volatile.Read(ref _current);
    public CoverImage? Cover(string revision) { var cover = Volatile.Read(ref _cover); return cover?.Revision == revision ? cover : null; }
    public void Start() => _worker = Task.Run(PollAsync);
    static string Text(string? text) => new((text ?? "").Where(c => !char.IsControl(c)).Take(240).ToArray());
    async Task PollAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(_stop.Token);
            var lastCoverCheck = DateTimeOffset.MinValue;
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var session = manager.GetCurrentSession();
                    if (session?.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        session = manager.GetSessions().FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) ?? session;
                    if (session == null) { Volatile.Write(ref _current, NowPlayingInfo.Empty); Volatile.Write(ref _cover, null); }
                    else
                    {
                        var properties = await session.TryGetMediaPropertiesAsync().AsTask(_stop.Token).WaitAsync(TimeSpan.FromSeconds(3), _stop.Token);
                        var title = Text(properties.Title); var artist = Text(properties.Artist); var album = Text(properties.AlbumTitle); var source = Text(session.SourceAppUserModelId);
                        var previous = Current;
                        var changed = title != previous.Title || artist != previous.Artist || album != previous.Album || source != previous.Source;
                        var cover = changed ? null : Volatile.Read(ref _cover);
                        if (changed || cover == null || DateTimeOffset.UtcNow - lastCoverCheck > TimeSpan.FromSeconds(30))
                        {
                            cover = await ReadCoverAsync(properties.Thumbnail); lastCoverCheck = DateTimeOffset.UtcNow;
                        }
                        var timeline = session.GetTimelineProperties();
                        Volatile.Write(ref _cover, cover);
                        Volatile.Write(ref _current, new NowPlayingInfo(title, artist, album, source,
                            session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                            cover?.Revision, Math.Max(0, timeline.Position.TotalSeconds), Math.Max(0, timeline.EndTime.TotalSeconds)));
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) { AppLog.Write("Read Windows media metadata", ex); }
                await Task.Delay(2000, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { AppLog.Write("Windows media session unavailable", ex); }
    }
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
    public async ValueTask DisposeAsync() { _stop.Cancel(); if (_worker != null) await _worker.ConfigureAwait(false); _stop.Dispose(); }
}
