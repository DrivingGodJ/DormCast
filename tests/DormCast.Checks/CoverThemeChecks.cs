using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DormCast.Hosting;
using DormCast.Networking;

static class CoverThemeChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }

    public static async Task RunAsync()
    {
        var nic = LanInterface.Discover().FirstOrDefault() ?? throw new Exception("No LAN interface for cover theme checks");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var mock = new CoverDemoHostController();
        await using var host = new BroadcastHost();
        await host.StartAsync(new BroadcastOptions(Port: 18098, RemoteControlEnabled: true), nic, mock);
        var ipv4 = new Uri(host.IpJoinUrl!);
        var token = ipv4.Fragment[6..];

        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var html = await http.GetStringAsync(new Uri(ipv4, "/"), deadline.Token);
        var themeJs = await http.GetStringAsync(new Uri(ipv4, "/theme.js"), deadline.Token);
        var mediaJs = await http.GetStringAsync(new Uri(ipv4, "/media.js"), deadline.Token);
        var css = await http.GetStringAsync(new Uri(ipv4, "/style.css"), deadline.Token);

        var themeScriptIdx = html.IndexOf("/theme.js", StringComparison.Ordinal);
        var mediaScriptIdx = html.IndexOf("/media.js", StringComparison.Ordinal);
        Check(themeScriptIdx > 0 && mediaScriptIdx > themeScriptIdx, "index.html loads /theme.js before /media.js");
        Check(themeJs.Contains("DormCastTheme") && themeJs.Contains("extractDominantColor") && themeJs.Contains("applyFromImage"), "/theme.js exposes dominant color extraction and theme helpers");
        Check(mediaJs.Contains("DormCastTheme") && mediaJs.Contains("applyFromImage"), "/media.js integrates cover load/clear with DormCastTheme");
        Check(css.Contains("--bg:#101612") && css.Contains("--accent:#c9f37f") && css.Contains("--accent-hover:#dcffac") && css.Contains("--ambient:transparent"), "style.css preserves exact default green theme tokens");

        var firstRevision = mock.NowPlaying.CoverRevision!;
        using (var req = new HttpRequestMessage(HttpMethod.Get, new Uri(ipv4, $"/cover/{firstRevision}")))
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(req, deadline.Token);
            var bytes = await resp.Content.ReadAsByteArrayAsync(deadline.Token);
            Check(resp.StatusCode == HttpStatusCode.OK && resp.Content.Headers.ContentType?.MediaType == "image/png" && NowPlayingService.ImageType(bytes) == "image/png",
                "authenticated cover endpoint serves valid PNG cover for theme extraction");
        }

        await host.StopAsync();
    }

    public static async Task RunDemoServerAsync(int port, int seconds)
    {
        var nic = LanInterface.Discover().FirstOrDefault() ?? throw new Exception("No LAN interface for cover demo server");
        var mock = new CoverDemoHostController();
        await using var host = new BroadcastHost();
        await host.StartAsync(new BroadcastOptions(Port: port, RemoteControlEnabled: true), nic, mock);
        Console.WriteLine($"DEMO_URL={host.IpJoinUrl}");
        Console.Out.Flush();
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 300)));
        await host.StopAsync();
    }

    sealed class CoverDemoHostController : IHostController
    {
        sealed record DemoTrack(string Title, string Artist, string Album, CoverImage? Cover);
        readonly DemoTrack[] _tracks;
        int _index;
        bool _playing = true;
        float _level = 0.65f;
        bool _muted;

        public CoverDemoHostController()
        {
            _tracks =
            [
                new("红玫瑰 · Crimson Nights", "测试乐队 A", "烈焰专辑 (红封面)", MakeCover((x, y, w, h) =>
                {
                    if (x < 4 || x >= w - 4 || y < 4 || y >= h - 4) return (24, 18, 20);
                    var t = (double)y / h;
                    return ((byte)(220 - t * 45), (byte)(42 + t * 22), (byte)(64 + t * 20));
                })),
                new("深海回响 · Cobalt Drift", "测试乐队 B", "蔚蓝专辑 (蓝封面)", MakeCover((x, y, w, h) =>
                {
                    var dx = (double)x / w - 0.5;
                    var dy = (double)y / h - 0.5;
                    var d = Math.Min(1.0, Math.Sqrt(dx * dx + dy * dy) * 1.4);
                    return ((byte)(28 + (1 - d) * 38), (byte)(92 + (1 - d) * 68), (byte)(205 + (1 - d) * 45));
                })),
                new("落日余晖 · Amber Horizon", "测试乐队 C", "暖阳专辑 (橙金封面)", MakeCover((x, y, w, h) =>
                {
                    var t = (double)y / h;
                    return ((byte)(240 - t * 35), (byte)(145 - t * 55), (byte)(36 + t * 18));
                })),
                new("紫夜星云 · Violet Echo", "测试乐队 D", "星云专辑 (紫封面)", MakeCover((x, y, w, h) =>
                {
                    var t = ((double)x + y) / (w + h);
                    return ((byte)(132 + t * 42), (byte)(56 + t * 24), (byte)(212 - t * 28));
                })),
                new("黑白胶片 · Monochrome Film", "测试乐队 E", "无彩专辑 (黑白封面)", MakeCover((x, y, w, h) =>
                {
                    var stripe = ((x / 8) + (y / 8)) % 2 == 0;
                    byte v = stripe ? (byte)228 : (byte)36;
                    return (v, v, v);
                })),
                new("无封面纯广播 · Default Green", "系统音频", "", null)
            ];
        }

        public event Action? StateChanged;
        public NowPlayingInfo NowPlaying
        {
            get
            {
                var t = _tracks[_index];
                return new(t.Title, t.Artist, t.Album, "demo.player", _playing, t.Cover?.Revision, 42, 240, true, true, true, true, true);
            }
        }
        public HostControlState ControlState => new(true, new HostVolumeState(true, _level, _muted, "Demo Speaker"));
        public CoverImage? Cover(string revision) => _tracks.Select(t => t.Cover).FirstOrDefault(c => c?.Revision == revision);
        public void Start() { }

        public Task<HostControlResult> ExecuteAsync(string action, float? level, float? delta, bool? muted, CancellationToken ct = default)
        {
            switch (action)
            {
                case "play": _playing = true; break;
                case "pause": _playing = false; break;
                case "toggle": _playing = !_playing; break;
                case "next": _index = (_index + 1) % _tracks.Length; break;
                case "previous": _index = (_index - 1 + _tracks.Length) % _tracks.Length; break;
                case "volume": if (level is { } v) { _level = Math.Clamp(v, 0f, 1f); if (v > 0) _muted = false; } break;
                case "volume-step": if (delta is { } d) { _level = Math.Clamp(_level + d, 0f, 1f); if (_level > 0) _muted = false; } break;
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

    static CoverImage MakeCover(Func<int, int, int, int, (byte r, byte g, byte b)> pixel)
    {
        var bytes = EncodeRgbPng(64, 64, pixel);
        var rev = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16];
        return new CoverImage(rev, "image/png", bytes);
    }

    static byte[] EncodeRgbPng(int width, int height, Func<int, int, int, int, (byte r, byte g, byte b)> pixel)
    {
        using var ms = new MemoryStream();
        ms.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // RGB
        WriteChunk(ms, "IHDR", ihdr);

        byte[] idat;
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                var row = new byte[1 + width * 3];
                for (var y = 0; y < height; y++)
                {
                    row[0] = 0;
                    for (var x = 0; x < width; x++)
                    {
                        var (r, g, b) = pixel(x, y, width, height);
                        var o = 1 + x * 3;
                        row[o] = r; row[o + 1] = g; row[o + 2] = b;
                    }
                    zlib.Write(row);
                }
            }
            idat = compressed.ToArray();
        }
        WriteChunk(ms, "IDAT", idat);
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    static void WriteChunk(Stream output, string type, byte[] data)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), data.Length);
        Encoding.ASCII.GetBytes(type, header.AsSpan(4, 4));
        output.Write(header);
        output.Write(data);
        var crc = Crc32(header.AsSpan(4, 4), data);
        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint crc = 0xffffffff;
        foreach (var v in a) crc = UpdateCrc(crc, v);
        foreach (var v in b) crc = UpdateCrc(crc, v);
        return crc ^ 0xffffffff;
    }

    static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        return crc;
    }
}
