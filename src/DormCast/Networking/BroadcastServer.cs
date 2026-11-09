using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using DormCast.Audio;
using DormCast.Hosting;
using DormCast.Synchronization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DormCast.Networking;

public sealed class BroadcastServer(BroadcastOptions options, LanInterface nic, AudioEngine audio) : IAsyncDisposable
{
    readonly JoinIdentity _identity = JoinIdentity.Persistent;
    readonly byte[] _token = Convert.FromHexString(JoinIdentity.Persistent.Token);
    readonly StableAddressPublisher _address = new();
    readonly NowPlayingService _music = new();
    readonly ConcurrentDictionary<string, ClientSession> _clients = new();
    readonly ConcurrentDictionary<string, Task> _tasks = new();
    readonly SemaphoreSlim _slots = new(16);
    readonly CancellationTokenSource _stop = new();
    WebApplication? _app;
    public BroadcastOptions Options => options;
    public SyncCoordinator Synchronization { get; } = new();
    public LanInterface Interface => nic;
    public double NowMs => audio.NowMs;
    public object AudioSnapshot => audio.Snapshot();
    public int ConnectedCount => _clients.Values.Count(c => c.Connected);
    public object[] Clients => _clients.Values.Select(c => c.Snapshot()).ToArray();
    public NowPlayingInfo NowPlaying => _music.Current;
    public string AddressStatus => _address.Status;
    public string BaseUrl => $"http://{nic.Address}:{options.Port}";
    public string JoinUrl => $"http://{_identity.HostName}:{options.Port}/#join={_identity.Token}";
    public string IpJoinUrl => $"{BaseUrl}/#join={_identity.Token}";
    public bool Authenticate(string? value)
    {
        if (value?.Length != 64) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(value), _token); } catch (FormatException) { return false; }
    }
    public async Task StartAsync()
    {
        options.Validate();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(nic.Address, options.Port);
            k.Limits.MaxRequestBodySize = 32768;
            k.Limits.MaxConcurrentConnections = 48;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(8);
        });
        _app = builder.Build();
        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(45) });
        _app.Run(HandleAsync);
        await _app.StartAsync(_stop.Token).ConfigureAwait(false);
        _address.Start(nic, _identity, options.Port); _music.Start();
        audio.FrameReady += FanOut;
    }
    void FanOut(EncodedFrame frame) { foreach (var client in _clients.Values) client.Enqueue(frame); }
    async Task HandleAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;
        if (!nic.Contains(context.Connection.RemoteIpAddress) || (host != nic.Address.ToString() && !host.Equals(_identity.HostName, StringComparison.OrdinalIgnoreCase)) || context.Request.Host.Port != options.Port)
        { context.Response.StatusCode = 403; return; }
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] = $"default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self' ws://{context.Request.Host}; media-src 'self' blob:; img-src 'self' data: blob:; frame-ancestors 'none'; base-uri 'none'";
        if (context.Request.Path == "/signal" && context.WebSockets.IsWebSocketRequest)
        {
            if (_stop.IsCancellationRequested) { context.Response.StatusCode = 503; return; }
            if (context.Request.Headers.Origin != $"http://{context.Request.Host}") { context.Response.StatusCode = 403; return; }
            if (!await _slots.WaitAsync(0).ConfigureAwait(false)) { context.Response.StatusCode = 429; return; }
            try
            {
                var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                var session = new ClientSession(socket, this, context.Connection.RemoteIpAddress!);
                _clients[session.Id] = session;
                var task = session.RunAsync(_stop.Token); _tasks[session.Id] = task;
                try { await task.ConfigureAwait(false); }
                finally { _clients.TryRemove(session.Id, out _); _tasks.TryRemove(session.Id, out _); Synchronization.Remove(session.Id); }
            }
            finally { _slots.Release(); }
            return;
        }
        if (context.Request.Method != "GET") { context.Response.StatusCode = 405; return; }
        if (context.Request.Path.StartsWithSegments("/cover", out var coverPath))
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || !Authenticate(authorization[7..])) { context.Response.StatusCode = 401; return; }
            var cover = _music.Cover(coverPath.Value?.Trim('/') ?? "");
            if (cover == null) { context.Response.StatusCode = 404; return; }
            context.Response.ContentType = cover.ContentType; await context.Response.Body.WriteAsync(cover.Bytes, context.RequestAborted).ConfigureAwait(false); return;
        }
        var (file, mime) = context.Request.Path.Value switch
        {
            "/" or "/index.html" => ("index.html", "text/html; charset=utf-8"),
            "/app.js" => ("app.js", "text/javascript; charset=utf-8"),
            "/sync.js" => ("sync.js", "text/javascript; charset=utf-8"),
            "/media.js" => ("media.js", "text/javascript; charset=utf-8"),
            "/style.css" => ("style.css", "text/css; charset=utf-8"),
            "/apple-touch-icon.png" => ("apple-touch-icon.png", "image/png"),
            "/favicon.png" => ("favicon.png", "image/png"),
            "/icon-192.png" => ("icon-192.png", "image/png"),
            "/icon-512.png" => ("icon-512.png", "image/png"),
            "/manifest.webmanifest" => ("manifest.webmanifest", "application/manifest+json"),
            _ => ("", "")
        };
        if (file == "") { context.Response.StatusCode = 404; return; }
        await using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream($"DormCast.wwwroot.{file}")!;
        context.Response.ContentType = mime;
        await resource.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        audio.FrameReady -= FanOut;
        await _music.DisposeAsync().ConfigureAwait(false); _address.Dispose();
        await Task.WhenAll(_clients.Values.Select(c => c.EndAsync())).ConfigureAwait(false);
        // Give the serialized writers one bounded interval to deliver the end notice.
        var remaining = Task.WhenAll(_tasks.Values);
        if (await Task.WhenAny(remaining, Task.Delay(800)).ConfigureAwait(false) != remaining) _stop.Cancel();
        _stop.Cancel();
        try { await remaining.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception ex) { AppLog.Write("Session cleanup", ex); }
        if (_app != null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _app.StopAsync(timeout.Token).ConfigureAwait(false); }
            finally { await _app.DisposeAsync().ConfigureAwait(false); }
        }
        _stop.Dispose(); _slots.Dispose();
    }
}
