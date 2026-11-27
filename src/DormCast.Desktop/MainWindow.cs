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
    readonly CancellationTokenSource _exitCancel = new();
    readonly nint _window;
    bool _busy, _exiting, _presenting, _ticking, _dialogOpen, _updatingOutputs;
    long _lastInput;
    string? _coverRevision;
    AudioDeviceChoice? _virtualCable;
    static readonly Version CurrentVersion = NormalizeVersion(typeof(MainWindow).Assembly.GetName().Version);
    static Version NormalizeVersion(Version? v) => v == null ? BroadcastHost.AppVersion : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    public MainWindow()
    {
        Title = $"宿舍之音 · DormCast {CurrentVersion} · WinUI 3";
        _window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "dormcast.ico"));
        var scale = GetDpiForWindow(_window) / 96.0;
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(1160 * scale), display.Width - 48); var height = Math.Min((int)(860 * scale), display.Height - 48);
        AppWindow.Resize(new SizeInt32(width, height)); AppWindow.Move(new PointInt32(display.X + (display.Width - width) / 2, display.Y + (display.Height - height) / 2));
        BuildLayout(); Content = _root; InitializeTheme();
        _name.Text = _options.Name; _port.Value = _options.Port;
        _hostPlayback.IsChecked = _options.HostPlayback;
        _volume = new SpeakerVolumeController(_options.OutputDeviceId); _music.Start();
        _tray = new NativeTray(_window, DispatcherQueue, ShowMain, StopAsync, ExitAsync);
        AppWindow.Closing += (_, e) => { if (!_exiting) { e.Cancel = true; AppWindow.Hide(); } };
        _start.Click += async (_, _) => { if (_host.Running) await StopAsync(); else await StartAsync(); };
        _copy.Click += (_, _) => Copy(DisplayedJoinUrl);
        _joinMode.SelectionChanged += async (_, e) => { if (e.AddedItems.Count > 0) await SwitchJoinModeAsync(Equals(e.AddedItems[0], _joinMode.Items[1])); };
        _refresh.Click += async (_, _) => await RefreshNetworkAsync(); _settings.Click += async (_, _) => await SettingsAsync(); _update.Click += async (_, _) => await CheckUpdateAsync();
        _driverHelp.Click += async (_, _) => await DriverGuideAsync();
        _captureDevice.SelectionChanged += (_, _) => OnAudioSelectionChanged();
        _output.SelectionChanged += (_, _) => OnAudioSelectionChanged();
        _hostPlayback.Click += (_, _) => OnAudioSelectionChanged();
        _slider.ValueChanged += (_, _) => { if (_presenting || !_slider.IsEnabled) return; _lastInput = Stopwatch.GetTimestamp(); _percent.Text = $"{_slider.Value:0}%"; _volume.RequestLevel((float)_slider.Value / 100); };
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(500); _timer.Tick += async (_, _) => await TickAsync(); _timer.Start();
        _copy.IsEnabled = false; _ = InitializeAsync();
    }
    async Task InitializeAsync()
    {
        try
        {
            await Task.Run(DefaultAudioRoute.RecoverIfNeeded);
            await RefreshNetworkAsync();
            if (_virtualCable == null && _captureDevice.SelectedItem == null) await DriverGuideAsync();
            _ = CheckUpdateOnStartupAsync();
        }
        catch (Exception ex) { _detail.Text = ex.Message; AppLog.Write("Desktop initialization", ex); }
    }
    void OnAudioSelectionChanged()
    {
        if (_updatingOutputs || _host.Running) return;
        var hostSound = _hostPlayback.IsChecked == true;
        var chosenCapture = _captureDevice.SelectedItem as AudioDeviceChoice;
        var chosenOutput = _output.SelectedItem as AudioDeviceChoice;
        if (chosenOutput != null && !chosenOutput.IsVirtual)
            _volume.Configure(chosenOutput.Id);
        _options = _options with
        {
            SyncEnabled = true,
            HostPlayback = hostSound,
            CaptureDeviceId = chosenCapture?.Id ?? _options.CaptureDeviceId,
            OutputDeviceId = chosenOutput != null && !chosenOutput.IsVirtual ? chosenOutput.Id : _options.OutputDeviceId
        };
        if (chosenCapture != null && (!hostSound || (chosenOutput != null && !chosenOutput.IsVirtual && chosenOutput.Id != chosenCapture.Id)))
        {
            var updated = _options;
            _ = Task.Run(() => { try { updated.Save(); } catch (Exception ex) { AppLog.Write("Save audio route", ex); } });
        }
        UpdateCaptureHint();
        UpdateStartAvailability();
    }
    void UpdateCaptureHint()
    {
        var hostSound = _hostPlayback.IsChecked == true;
        var capture = _captureDevice.SelectedItem as AudioDeviceChoice;
        _output.IsEnabled = !_busy && !_host.Running && hostSound;
        if (capture == null)
        {
            _virtualStatus.Text = "未检测到 VB-CABLE 虚拟声卡；请安装驱动，或在上方选择 MIXLINE 等输入设备。";
            return;
        }
        if (capture.IsCaptureInput)
        {
            _virtualStatus.Text = hostSound
                ? $"使用自定义输入「{capture.Name}」采集，保持系统默认输出不变，并通过本机扬声器统一同步播放。"
                : $"使用自定义输入「{capture.Name}」仅向收听端广播（电脑不发声）；若混音软件同时直出扬声器将不参与延迟对齐。";
            return;
        }
        _virtualStatus.Text = hostSound
            ? $"使用虚拟声卡「{capture.Name}」自动接管采集，并在所选真实扬声器同步播放（停止后恢复）。"
            : $"使用虚拟声卡「{capture.Name}」接管系统声音仅向收听端广播（电脑本机静音不发声）。";
    }
    void UpdateStartAvailability()
    {
        var hasNic = _nic.SelectedItem is LanInterface;
        var hasCapture = _captureDevice.SelectedItem is AudioDeviceChoice;
        var hostSound = _hostPlayback.IsChecked == true;
        var hasOutput = _output.SelectedItem is AudioDeviceChoice d && !d.IsVirtual;
        var hasVirtual = _virtualCable != null;
        _driverHelp.Visibility = hasVirtual ? Visibility.Collapsed : Visibility.Visible;
        _output.IsEnabled = !_busy && !_host.Running && hostSound;
        if (_busy)
        {
            _start.IsEnabled = false;
            _start.Content = "正在处理…";
            return;
        }
        if (_host.Running)
        {
            _start.IsEnabled = true;
            _start.Content = "停止广播";
            return;
        }
        if (!hasCapture)
        {
            _start.IsEnabled = false;
            _start.Content = "请安装虚拟声卡或选择采集设备";
            return;
        }
        if (hostSound && !hasOutput)
        {
            _start.IsEnabled = false;
            _start.Content = "请选择真实输出设备";
            return;
        }
        _start.Content = "开始广播";
        _start.IsEnabled = hasNic;
    }
    async Task RefreshNetworkAsync()
    {
        if (_host.Running || _busy) return; _refresh.IsEnabled = false;
        try
        {
            var selectedNic = (_nic.SelectedItem as LanInterface)?.Address;
            var preferredCapture = (_captureDevice.SelectedItem as AudioDeviceChoice)?.Id ?? _options.CaptureDeviceId;
            var preferredOutput = (_output.SelectedItem as AudioDeviceChoice)?.Id ?? _options.OutputDeviceId;
            var snapshot = await Task.Run(() =>
            {
                var nics = LanInterface.Discover();
                var renders = AudioDevices.RenderDevices();
                var inputs = AudioDevices.InputDevices();
                var captures = AudioDevices.CaptureDevices(renders, inputs);
                var defaultId = AudioDevices.DefaultRenderId();
                var virtualCable = AudioDevices.FindVirtualCaptureDevice(renders);
                var chosenCapture = AudioDevices.ResolveCaptureDevice(preferredCapture, renders, inputs);
                var physical = AudioDevices.PhysicalOutputDevices(renders);
                var chosenOutput = AudioDevices.ResolvePhysicalOutput(preferredOutput, renders, defaultId);
                return new { nics, virtualCable, captures, chosenCapture, physical, chosenOutput };
            });

            _nic.ItemsSource = snapshot.nics;
            _nic.SelectedItem = snapshot.nics.FirstOrDefault(n => n.Address.Equals(selectedNic)) ?? snapshot.nics.FirstOrDefault();
            _network.Text = snapshot.nics.Count == 0 ? "没有可用的私网 IPv4，请连接 Wi-Fi。" : $"{(_nic.SelectedItem as LanInterface)?.Address} · 仅局域网访问";

            _virtualCable = snapshot.virtualCable;
            _updatingOutputs = true;
            try
            {
                _captureDevice.ItemsSource = snapshot.captures;
                _captureDevice.SelectedItem = snapshot.chosenCapture;
                _output.ItemsSource = snapshot.physical;
                _output.SelectedItem = snapshot.chosenOutput;
                _hostPlayback.IsChecked = _options.HostPlayback;
            }
            finally { _updatingOutputs = false; }

            _volume.Configure(snapshot.chosenOutput?.Id);
            if (snapshot.chosenCapture != null)
            {
                var hostSound = _hostPlayback.IsChecked == true;
                var nextOptions = _options with
                {
                    SyncEnabled = true,
                    HostPlayback = hostSound,
                    CaptureDeviceId = snapshot.chosenCapture.Id,
                    OutputDeviceId = snapshot.chosenOutput?.Id
                };
                _options = nextOptions;
                if (!hostSound || snapshot.chosenOutput != null)
                    await Task.Run(() => { try { nextOptions.Save(); } catch (Exception ex) { AppLog.Write("Save auto audio route", ex); } });
                else
                    _detail.Text = "未检测到可用的实际扬声器或耳机，请连接输出设备或取消勾选「电脑本机发声」。";
            }
            else
            {
                _detail.Text = "未检测到虚拟声卡（VB-CABLE），请点击「查看虚拟声卡安装指引」或选择 MIXLINE 等采集设备。";
            }
            UpdateCaptureHint();
            UpdateStartAvailability();
        }
        finally { _refresh.IsEnabled = true; }
    }
    void SetBusy(bool value)
    {
        _busy = value;
        foreach (var control in new Control[] { _name, _nic, _captureDevice, _hostPlayback, _port, _settings, _refresh }) control.IsEnabled = !value && !_host.Running;
        _output.IsEnabled = !value && !_host.Running && _hostPlayback.IsChecked == true;
        _joinMode.IsEnabled = _copy.IsEnabled = !value && _host.Running;
        UpdateStartAvailability();
    }
    async Task StartAsync()
    {
        if (_busy || _nic.SelectedItem is not LanInterface nic) return;
        var hostSound = _hostPlayback.IsChecked == true;
        var selectedCaptureId = (_captureDevice.SelectedItem as AudioDeviceChoice)?.Id ?? _options.CaptureDeviceId;
        var selectedOutputId = (_output.SelectedItem as AudioDeviceChoice)?.Id ?? _options.OutputDeviceId;
        var audioCheck = await Task.Run(() =>
        {
            var renders = AudioDevices.RenderDevices();
            var inputs = AudioDevices.InputDevices();
            return new { virtualCable = AudioDevices.FindVirtualCaptureDevice(renders), renders, inputs };
        });
        _virtualCable = audioCheck.virtualCable;
        AudioDeviceChoice chosenCapture;
        try
        {
            chosenCapture = AudioDevices.ValidateRoute(selectedCaptureId, hostSound ? selectedOutputId : null, hostSound, audioCheck.renders, audioCheck.inputs);
        }
        catch (Exception ex)
        {
            _detail.Text = ex.Message;
            UpdateCaptureHint();
            UpdateStartAvailability();
            if (_virtualCable == null && string.IsNullOrWhiteSpace(selectedCaptureId)) await DriverGuideAsync();
            return;
        }
        SetBusy(true);
        try
        {
            var options = _options with
            {
                Name = _name.Text.Trim(),
                Port = (int)_port.Value,
                FrameMs = AudioEngine.DefaultFrameMs,
                SyncEnabled = true,
                HostPlayback = hostSound,
                CaptureDeviceId = chosenCapture.Id,
                OutputDeviceId = hostSound ? selectedOutputId : _options.OutputDeviceId
            };
            options.Validate();
            await Task.Run(async () => { options.Save(); await _host.StartAsync(options, nic); }); _options = options;
            _useIpv4Join = false; _joinMode.SelectedIndex = 0; await RefreshJoinAsync();
            _tray.SetBroadcasting(true); _state.Text = "● 正在广播";
            _detail.Text = chosenCapture.IsCaptureInput
                ? (hostSound ? $"已从输入设备「{chosenCapture.Name}」采集并开启本机同步播放；请确保所有设备连接在同一个 Wi-Fi / 局域网中。" : $"已从输入设备「{chosenCapture.Name}」静默广播（电脑不发声）；请确保所有设备连接在同一个 Wi-Fi / 局域网中。")
                : (hostSound ? "已自动启用虚拟声卡同步广播；请确保所有设备连接在同一个 Wi-Fi / 局域网中。关闭窗口会隐藏到托盘。" : "已开启虚拟声卡静默广播（电脑不发声）；请确保所有设备连接在同一个 Wi-Fi / 局域网中。");
        }
        catch (Exception ex) { _detail.Text = $"无法开始广播：{ex.Message}"; AppLog.Write("Start failed", ex); }
        finally { SetBusy(false); }
    }
    async Task StopAsync()
    {
        if (_busy && !_exiting) return; SetBusy(true);
        try { await Task.Run(async () => await _host.StopAsync()); _detail.Text = "广播已结束，固定链接下次仍可使用。"; }
        catch (Exception ex) { _detail.Text = ex.Message; AppLog.Write("Stop failed", ex); }
        finally { _tray.SetBroadcasting(false); _state.Text = "未广播"; _qr.Source = null; _url.Text = "广播已结束，下次仍可使用固定链接。"; SetBusy(false); }
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
        if (_exiting) return; _exiting = true; _exitCancel.Cancel(); _timer.Stop(); _root.IsHitTestVisible = false;
        try { await Task.Run(async () => { await _host.DisposeAsync(); _volume.Dispose(); await _music.DisposeAsync(); }); }
        catch (Exception ex) { AppLog.Write("Desktop cleanup", ex); }
        finally { _appearance.ColorValuesChanged -= AppearanceChanged; _tray.Dispose(); _exitCancel.Dispose(); Close(); Application.Current.Exit(); }
    }
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint window);
}
