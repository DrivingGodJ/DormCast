using DormCast.Networking;
namespace DormCast.Hosting;
public static class HeadlessRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? Value(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var interfaces = LanInterface.Discover();
        var nic = Value("--bind") is { } ip ? interfaces.FirstOrDefault(n => n.Address.ToString() == ip) : interfaces.FirstOrDefault();
        if (nic == null) throw new ArgumentException("没有可用的私网 IPv4 地址。");
        var options = new BroadcastOptions(Value("--name") ?? "宿舍之音", int.Parse(Value("--port") ?? "8080"), int.Parse(Value("--frame") ?? "20"),
            SyncEnabled: !args.Contains("--no-sync"), HostPlayback: args.Contains("--host-playback"), CaptureDeviceId: Value("--capture-device"), OutputDeviceId: Value("--output-device"),
            HostHardwareCorrectionMs: int.Parse(Value("--host-hardware-ms") ?? "0"));
        using var stop = new CancellationTokenSource();
        if (Value("--duration") is { } seconds) stop.CancelAfter(TimeSpan.FromSeconds(int.Parse(seconds)));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        await using var host = new BroadcastHost();
        await host.StartAsync(options, nic).ConfigureAwait(false);
        if (Value("--join-file") is { } joinFile) File.WriteAllText(joinFile, host.JoinUrl);
        Console.WriteLine($"DormCast listening on {nic.Address}:{options.Port}. Ctrl+C stops capture and all listeners.");
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(1000, stop.Token).ConfigureAwait(false);
                if (Value("--stop-file") is { } stopFile && File.Exists(stopFile)) break;
                if (Value("--diagnostics") is { } path) File.WriteAllText(path, host.Diagnostics());
                if (!host.NetworkAvailable) throw new IOException("所选网卡已断开。");
                if (host.AudioFailure is { } failure) throw failure;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            if (Value("--diagnostics") is { } path) File.WriteAllText(path, host.Diagnostics());
            await host.StopAsync().ConfigureAwait(false);
        }
        return 0;
    }
}
