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
            var devices = await Task.Run(AudioDevices.RenderDevices);
            var virtualCable = AudioDevices.FindVirtualCaptureDevice(devices);
            var physicalOutputs = AudioDevices.PhysicalOutputDevices(devices);
            var virtualInfo = Label(virtualCable != null
                ? $"虚拟声卡（由软件自动选择）：{virtualCable.Name}"
                : "虚拟声卡（由软件自动选择）：未检测到 VB-CABLE，无法启动广播。", 13);
            var remote = new CheckBox { Content = "允许浏览器控制主机播放与音量", IsChecked = _options.RemoteControlEnabled };
            var output = new ComboBox { Header = "输出设备（本机实际扬声器 / 耳机）", ItemsSource = physicalOutputs, HorizontalAlignment = HorizontalAlignment.Stretch };
            output.SelectedItem = AudioDevices.ResolvePhysicalOutput((_output.SelectedItem as AudioDeviceChoice)?.Id ?? _options.OutputDeviceId, devices);
            var correction = new NumberBox { Header = "主机额外硬件延迟（ms）", Minimum = 0, Maximum = 1000, Value = _options.HostHardwareCorrectionMs };
            var error = Label("广播时自动使用虚拟声卡采集并延迟输出到所选设备，停止后恢复默认输出。", 13);
            var driver = ActionButton(virtualCable == null ? "立即安装虚拟声卡" : "打开虚拟声卡安装包");
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
                Content = Stack(virtualInfo, output, remote, correction, error, driver),
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            dialog.PrimaryButtonClick += (_, e) =>
            {
                try
                {
                    if (output.SelectedItem is not AudioDeviceChoice chosen || chosen.IsVirtual)
                        throw new ArgumentException("请选择一个本机实际扬声器或耳机作为输出设备。");
                    if (virtualCable == null)
                        throw new ArgumentException("未检测到虚拟声卡（VB-CABLE），请先安装虚拟声卡后再保存。");
                    var updated = _options with
                    {
                        SyncEnabled = true,
                        HostPlayback = true,
                        RemoteControlEnabled = remote.IsChecked == true,
                        CaptureDeviceId = virtualCable.Id,
                        OutputDeviceId = chosen.Id,
                        HostHardwareCorrectionMs = (int)correction.Value
                    };
                    updated.Validate();
                    _options = updated;
                    _virtualCable = virtualCable;
                    _volume.Configure(updated.OutputDeviceId);
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
