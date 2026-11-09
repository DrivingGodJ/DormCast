using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using DormCast.Hosting;
using DormCast.Networking;

static class DualJoinChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    public static async Task RunAsync()
    {
        var nic = LanInterface.Discover().FirstOrDefault() ?? throw new Exception("No LAN interface for access checks");
        await using var host = new BroadcastHost();
        await host.StartAsync(new BroadcastOptions(Port: 18094), nic);
        var mdns = new Uri(host.JoinUrl!); var ipv4 = new Uri(host.IpJoinUrl!);
        Check(mdns.Host.EndsWith(".local") && IPAddress.TryParse(ipv4.Host, out var ip) && ip.Equals(nic.Address), "fixed and literal IPv4 join addresses are both available");
        Check(mdns.Fragment == ipv4.Fragment && mdns.Fragment.StartsWith("#join="), "both join methods preserve the same access credential");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        async Task<string> ReadPage(string authority)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ipv4, "/")); request.Headers.Host = authority;
            using var response = await http.SendAsync(request, deadline.Token);
            Check(response.StatusCode == HttpStatusCode.OK && response.Headers.Location == null, "join address serves directly without redirecting to the other scheme");
            return await response.Content.ReadAsStringAsync(deadline.Token);
        }
        var pages = await Task.WhenAll(ReadPage(mdns.Authority), ReadPage(ipv4.Authority));
        Check(pages[0] == pages[1], "mDNS and IPv4 receive the same browser client concurrently");
        async Task<ClientWebSocket> Connect(string authority, string name)
        {
            var socket = new ClientWebSocket();
            try
            {
                // Connect to the literal LAN address even for the alias request: this also runs
                // on hosts without an mDNS resolver, while testing both Host/Origin allowlists.
                socket.Options.SetRequestHeader("Host", authority); socket.Options.SetRequestHeader("Origin", "http://" + authority);
                await socket.ConnectAsync(new Uri($"ws://{ipv4.Authority}/signal"), deadline.Token);
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "hello", token = mdns.Fragment[6..], name }), WebSocketMessageType.Text, true, deadline.Token);
                var bytes = new byte[16384]; var count = 0; WebSocketReceiveResult received;
                do { received = await socket.ReceiveAsync(new ArraySegment<byte>(bytes, count, bytes.Length - count), deadline.Token); count += received.Count; } while (!received.EndOfMessage);
                using var welcome = JsonDocument.Parse(bytes.AsMemory(0, count));
                Check(welcome.RootElement.GetProperty("type").GetString() == "welcome", "join credential authenticates over the selected address");
                return socket;
            }
            catch { socket.Dispose(); throw; }
        }
        using var fixedClient = await Connect(mdns.Authority, "fixed address check");
        using var ipClient = await Connect(ipv4.Authority, "IPv4 address check");
        for (var i = 0; i < 4; i++) await ReadPage(i % 2 == 0 ? ipv4.Authority : mdns.Authority);
        Check(fixedClient.State == WebSocketState.Open && ipClient.State == WebSocketState.Open && host.Clients.Length == 2, "alternating addresses leaves both authenticated clients open together");
        await ipClient.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
        await ReadPage(mdns.Authority);
        Check(fixedClient.State == WebSocketState.Open, "an IPv4 client leaving does not close the fixed-address client");
        await fixedClient.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
        await host.StopAsync();
        using var rebind = new System.Net.Sockets.TcpListener(nic.Address, 18094); rebind.Start(); rebind.Stop();
        Check(true, "dual-access check releases its temporary listener");
    }
}
