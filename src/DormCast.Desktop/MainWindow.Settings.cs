using DormCast.Audio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DormCast.Desktop;

public sealed partial class MainWindow
{
    async Task SettingsAsync()
    {
        if (_busy || _host.Running || _dialogOpen) return;
        _dialogOpen = true;
        try
        {
            _settings.IsEnabled = false;
            var audio = await Task.Run(() =>
            {
                var renders = AudioDevices.RenderDevices();
                var inputs = AudioDevices.InputDevices();
                return new
                {
                    renders,
                    inputs,
                    virtualCable = AudioDevices.FindVirtualCaptureDevice(renders),
                    captures = AudioDevices.CaptureDevices(renders, inputs),
                    physicalOutputs = AudioDevices.PhysicalOutputDevices(renders)
                };
            });
            var capture = new ComboBox { Header = "采集设备（虚拟声卡 / MIXLINE 等输入）", ItemsSource = audio.captures, HorizontalAlignment = HorizontalAlignment.Stretch };
            capture.SelectedItem = AudioDevices.ResolveCaptureDevice((_captureDevice.SelectedItem as AudioDeviceChoice)?.Id ?? _options.CaptureDeviceId, audio.renders, audio.inputs);
            var hostPlayback = new CheckBox { Content = "电脑本机发声（开启时强制与所有设备统一同步）", IsChecked = _hostPlayback.IsChecked == true };
            var output = new ComboBox { Header = "真实输出设备（本机扬声器 / 耳机）", ItemsSource = audio.physicalOutputs, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = hostPlayback.IsChecked == true };
            output.SelectedItem = AudioDevices.ResolvePhysicalOutput((_output.SelectedItem as AudioDeviceChoice)?.Id ?? _options.OutputDeviceId, audio.renders);
            var correction = new NumberBox { Header = "主机额外硬件延迟（ms）", Minimum = 0, Maximum = 1000, Value = _options.HostHardwareCorrectionMs, IsEnabled = hostPlayback.IsChecked == true };
            hostPlayback.Click += (_, _) =>
            {
                var on = hostPlayback.IsChecked == true;
                output.IsEnabled = on;
                correction.IsEnabled = on;
            };
            var remote = new CheckBox { Content = "允许浏览器控制主机播放与音量", IsChecked = _options.RemoteControlEnabled };
            var error = Label("选择虚拟声卡时会在广播期间临时切换默认输出；选择 MIXLINE 等输入设备时不会改动系统默认设备。开启「电脑本机发声」即自动参与统一同步。", 13);
            var driver = ActionButton(audio.virtualCable == null ? "立即安装虚拟声卡" : "打开虚拟声卡安装包");
            driver.Click += (_, _) =>
            {
                if (!TryLaunchBundledVirtualCableInstaller(out var err)) error.Text = err;
                else error.Text = "已启动 VB-CABLE 官方安装程序，完成后请重启电脑或点击刷新。";
            };
            var dialog = new ContentDialog
            {
                XamlRoot = _root.XamlRoot,
                RequestedTheme = _root.ActualTheme,
                Title = "同步与输出设置",
                Content = Stack(capture, hostPlayback, output, correction, remote, error, driver),
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            dialog.PrimaryButtonClick += (_, e) =>
            {
                try
                {
                    var hostSound = hostPlayback.IsChecked == true;
                    var chosenCapture = capture.SelectedItem as AudioDeviceChoice;
                    var chosenOutput = output.SelectedItem as AudioDeviceChoice;
                    var validatedCapture = AudioDevices.ValidateRoute(chosenCapture?.Id, hostSound ? chosenOutput?.Id : null, hostSound, audio.renders, audio.inputs);
                    var updated = _options with
                    {
                        SyncEnabled = true,
                        HostPlayback = hostSound,
                        RemoteControlEnabled = remote.IsChecked == true,
                        CaptureDeviceId = validatedCapture.Id,
                        OutputDeviceId = hostSound ? chosenOutput!.Id : _options.OutputDeviceId,
                        HostHardwareCorrectionMs = (int)correction.Value
                    };
                    updated.Validate();
                    _options = updated;
                    _virtualCable = audio.virtualCable;
                    if (hostSound && chosenOutput != null) _volume.Configure(chosenOutput.Id);
                }
                catch (Exception ex) { e.Cancel = true; error.Text = ex.Message; }
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await Task.Run(_options.Save);
                await RefreshNetworkAsync();
                _detail.Text = "同步与输出设置已保存。";
            }
        }
        catch (Exception ex) { _detail.Text = ex.Message; }
        finally { _dialogOpen = false; _settings.IsEnabled = true; }
    }
    async Task SaveDiagnosticsAsync()
    {
        try { var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"DormCast-诊断-{DateTime.Now:yyyyMMdd-HHmmss}" }; picker.FileTypeChoices.Add("JSON", new List<string> { ".json" }); WinRT.Interop.InitializeWithWindow.Initialize(picker, _window); var file = await picker.PickSaveFileAsync(); if (file == null) return; var json = await Task.Run(_host.Diagnostics); await Windows.Storage.FileIO.WriteTextAsync(file, json); _detail.Text = "诊断已保存。"; }
        catch (Exception ex) { _detail.Text = ex.Message; }
    }
}
