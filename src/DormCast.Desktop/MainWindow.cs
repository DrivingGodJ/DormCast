using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DormCast.Audio;
using DormCast.Hosting;
using DormCast.Networking;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Streams;

namespace DormCast.Desktop;

public sealed partial class MainWindow : Window
{
    readonly BroadcastHost _host = new();
    readonly NowPlayingService _music = new();
    readonly SpeakerVolumeController _volume;
    readonly DispatcherQueueTimer _timer;
    readonly NativeTray _tray;
    BroadcastOptions _options = BroadcastOptions.Load();
    readonly bool _firstRun = !BroadcastOptions.HasSavedSettings;
    readonly nint _window;
    bool _busy, _exiting, _presenting, _ticking;
    long _lastInput;
    string? _coverRevision;
    public MainWindow()
    {
        Title = "宿舍之音 · DormCast 0.5.2 · WinUI 3";
        _window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "dormcast.ico"));
        var scale = GetDpiForWindow(_window) / 96.0;
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(1160 * scale), display.Width - 48); var height = Math.Min((int)(860 * scale), display.Height - 48);
        AppWindow.Resize(new SizeInt32(width, height)); AppWindow.Move(new PointInt32(display.X + (display.Width - width) / 2, display.Y + (display.Height - height) / 2));
        BuildLayout(); Content = _root; InitializeTheme();
        _name.Text = _options.Name; _port.Value = _options.Port;
        _frame.Items.Add("20ms · 稳定音乐"); _frame.Items.Add("10ms · 较低延迟"); _frame.SelectedIndex = _options.FrameMs == 10 ? 1 : 0;
        _volume = new SpeakerVolumeController(_options.OutputDeviceId); _music.Start();
        _tray = new NativeTray(_window, DispatcherQueue, ShowMain, StopAsync, ExitAsync);
        AppWindow.Closing += (_, e) => { if (!_exiting) { e.Cancel = true; AppWindow.Hide(); } };
        _start.Click += async (_, _) => { if (_host.Running) await StopAsync(); else await StartAsync(); };
        _copy.Click += (_, _) => Copy(_host.JoinUrl); _copyIp.Click += (_, _) => Copy(_host.IpJoinUrl);
        _refresh.Click += async (_, _) => await RefreshNetworkAsync(); _settings.Click += async (_, _) => await SettingsAsync(); _update.Click += async (_, _) => await CheckUpdateAsync();
        _slider.ValueChanged += (_, _) => { if (_presenting || !_slider.IsEnabled) return; _lastInput = Stopwatch.GetTimestamp(); _percent.Text = $"{_slider.Value:0}%"; _volume.RequestLevel((float)_slider.Value / 100); };
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(500); _timer.Tick += async (_, _) => await TickAsync(); _timer.Start();
        _copy.IsEnabled = _copyIp.IsEnabled = false; _ = InitializeAsync();
    }
    async Task InitializeAsync()
    {
        try
        {
            var defaults = await Task.Run(() =>
            {
                DefaultAudioRoute.RecoverIfNeeded(); var devices = AudioDevices.RenderDevices();
                var source = devices.FirstOrDefault(d => d.IsVirtual); var defaultId = AudioDevices.DefaultRenderId();
                var output = devices.FirstOrDefault(d => !d.IsVirtual && d.Id == defaultId) ?? devices.FirstOrDefault(d => !d.IsVirtual && d.Name.Contains("Realtek", StringComparison.OrdinalIgnoreCase)) ?? devices.FirstOrDefault(d => !d.IsVirtual);
                return new { source, output };
            });
            if (_firstRun && defaults.source != null && defaults.output != null) { _options = _options with { HostPlayback = true, CaptureDeviceId = defaults.source.Id, OutputDeviceId = defaults.output.Id }; _volume.Configure(defaults.output.Id); await Task.Run(_options.Save); }
            if (defaults.source == null) { _detail.Text = "主机同步播放需要虚拟声卡，请先完成驱动安装。"; await DriverGuideAsync(); }
            await RefreshNetworkAsync();
        }
        catch (Exception ex) { _detail.Text = ex.Message; AppLog.Write("Desktop initialization", ex); }
    }
    async Task RefreshNetworkAsync()
    {
        if (_host.Running || _busy) return; _refresh.IsEnabled = false;
        try { var selected = (_nic.SelectedItem as LanInterface)?.Address; var choices = await Task.Run(LanInterface.Discover); _nic.ItemsSource = choices; _nic.SelectedItem = choices.FirstOrDefault(n => n.Address.Equals(selected)) ?? choices.FirstOrDefault(); _network.Text = choices.Count == 0 ? "没有可用的私网 IPv4，请连接 Wi-Fi。" : $"{(_nic.SelectedItem as LanInterface)?.Address} · 仅局域网访问"; _start.IsEnabled = choices.Count > 0; }
        finally { _refresh.IsEnabled = true; }
    }
    void SetBusy(bool value)
    {
        _busy = value; foreach (var control in new Control[] { _name, _nic, _frame, _port, _settings, _refresh }) control.IsEnabled = !value && !_host.Running;
        _start.IsEnabled = !value; _start.Content = value ? "正在处理…" : _host.Running ? "停止广播" : "开始广播";
    }
    async Task StartAsync()
    {
        if (_busy || _nic.SelectedItem is not LanInterface nic) return; SetBusy(true);
        try
        {
            var options = _options with { Name = _name.Text.Trim(), Port = (int)_port.Value, FrameMs = _frame.SelectedIndex == 1 ? 10 : 20 }; options.Validate();
            await Task.Run(async () => { options.Save(); await _host.StartAsync(options, nic); }); _options = options;
            _url.Text = _host.JoinUrl; _copy.IsEnabled = _copyIp.IsEnabled = true;
            using var code = QRCodeGenerator.GenerateQrCode(_host.JoinUrl!, QRCodeGenerator.ECCLevel.M); using var png = new PngByteQRCode(code); _qr.Source = await DecodeAsync(png.GetGraphic(6), 368);
            _tray.SetBroadcasting(true); _state.Text = "● 正在广播"; _detail.Text = "固定链接可以收藏。关闭窗口会隐藏到托盘。";
        }
        catch (Exception ex) { _detail.Text = $"无法开始广播：{ex.Message}"; AppLog.Write("Start failed", ex); }
        finally { SetBusy(false); }
    }
    async Task StopAsync()
    {
        if (_busy && !_exiting) return; SetBusy(true);
        try { await Task.Run(async () => await _host.StopAsync()); _detail.Text = "广播已结束，固定链接下次仍可使用。"; }
        catch (Exception ex) { _detail.Text = ex.Message; AppLog.Write("Stop failed", ex); }
        finally { _tray.SetBroadcasting(false); _state.Text = "未广播"; _copy.IsEnabled = _copyIp.IsEnabled = false; _qr.Source = null; SetBusy(false); }
    }
    async Task TickAsync()
    {
        if (_exiting || _ticking) return; _ticking = true;
        try
        {
            var info = _music.Current; SetText(_song, string.IsNullOrWhiteSpace(info.Title) ? "你的音乐，正在这里。" : info.Title); SetText(_artist, string.IsNullOrWhiteSpace(info.Artist) ? "播放器未提供歌手信息" : info.Artist); SetText(_album, info.Album);
            if (_coverRevision != info.CoverRevision) { _coverRevision = info.CoverRevision; var art = _coverRevision == null ? null : _music.Cover(_coverRevision); _cover.Source = art == null ? null : await DecodeAsync(art.Bytes, 512); _coverFallback.Visibility = art == null ? Visibility.Visible : Visibility.Collapsed; }
            var volume = _volume.Current; _slider.IsEnabled = volume != null;
            if (volume != null) { SetText(_speaker, volume.Name); if (Stopwatch.GetElapsedTime(_lastInput).TotalMilliseconds > 500) { _presenting = true; _slider.Value = Math.Round(volume.Level * 100); _presenting = false; SetText(_percent, volume.Muted ? "静音" : $"{_slider.Value:0}%"); } }
            if (!_busy && _host.Running)
            {
                var snapshot = await Task.Run(() => new { network = _host.NetworkAvailable, failure = _host.AudioFailure, device = _host.DeviceName, count = _host.ConnectedCount, sync = _host.SyncState, address = _host.AddressStatus, rows = ClientDetail.Read(_host.Clients, _host.SyncState?.PlayoutTargetMs ?? 0) });
                if (!snapshot.network || snapshot.failure != null) { await StopAsync(); _detail.Text = snapshot.failure?.Message ?? "网卡已断开，请刷新后重新广播。"; }
                else { SetText(_capture, snapshot.device); SetText(_listeners, $"{snapshot.count} 台设备"); SetText(_sync, $"{snapshot.sync?.TargetMs:0} ms"); SetText(_network, $"{snapshot.address} · {snapshot.sync?.Direction switch { "decreasing" => "延迟正在回落", "settling" => "观察后回落", _ => "已连接" }}"); PresentClients(snapshot.rows); }
            }
            else if (!_host.Running) { SetText(_listeners, "0 台设备"); SetText(_sync, "等待测量"); PresentClients([]); }
        }
        catch (Exception ex) { AppLog.Write("Desktop snapshot", ex); }
        finally { _presenting = false; _ticking = false; }
    }
    static void SetText(TextBlock label, string value) { if (label.Text != value) label.Text = value; }
    static async Task<BitmapImage> DecodeAsync(byte[] bytes, int width)
    {
        using var stream = new InMemoryRandomAccessStream(); using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); }
        stream.Seek(0); var bitmap = new BitmapImage { DecodePixelWidth = width }; await bitmap.SetSourceAsync(stream); return bitmap;
    }
    void Copy(string? url) { if (url == null) return; var data = new DataPackage(); data.SetText(url); Clipboard.SetContent(data); _detail.Text = "链接已复制，只分享给同一局域网内的听众。"; }
    void ShowMain() { AppWindow.Show(); Activate(); }
    async Task ExitAsync()
    {
        if (_exiting) return; _exiting = true; _timer.Stop(); _root.IsHitTestVisible = false;
        try { await Task.Run(async () => { await _host.DisposeAsync(); _volume.Dispose(); await _music.DisposeAsync(); }); }
        catch (Exception ex) { AppLog.Write("Desktop cleanup", ex); }
        finally { _appearance.ColorValuesChanged -= AppearanceChanged; _tray.Dispose(); Close(); Application.Current.Exit(); }
    }
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint window);
}
