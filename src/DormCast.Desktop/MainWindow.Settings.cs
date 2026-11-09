using DormCast.Audio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

namespace DormCast.Desktop;

public sealed partial class MainWindow
{
    async Task SettingsAsync()
    {
        if (_busy || _host.Running) return;
        try
        {
            _settings.IsEnabled = false; var devices = await Task.Run(AudioDevices.RenderDevices);
            var sync = new CheckBox { Content = "按最慢设备统一同步", IsChecked = _options.SyncEnabled }; var host = new CheckBox { Content = "主机也延迟播放（需要虚拟声卡）", IsChecked = _options.HostPlayback };
            var capture = new ComboBox { Header = "采集设备（虚拟声卡）", ItemsSource = devices, HorizontalAlignment = HorizontalAlignment.Stretch }; var output = new ComboBox { Header = "主机实际扬声器", ItemsSource = devices.Where(d => !d.IsVirtual).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch };
            capture.SelectedItem = devices.FirstOrDefault(d => d.Id == _options.CaptureDeviceId) ?? devices.FirstOrDefault(d => d.IsVirtual); output.SelectedItem = devices.FirstOrDefault(d => d.Id == _options.OutputDeviceId && !d.IsVirtual) ?? devices.FirstOrDefault(d => !d.IsVirtual);
            var correction = new NumberBox { Header = "主机额外硬件延迟（ms）", Minimum = 0, Maximum = 1000, Value = _options.HostHardwareCorrectionMs }; var error = Label("广播时切换默认输出，停止后恢复；采集与实际扬声器必须不同。", 13);
            var driver = ActionButton("打开附带的官方驱动原包"); driver.Click += (_, _) => { var folder = Path.Combine(AppContext.BaseDirectory, "Drivers"); if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); else error.Text = "请使用正式安装包，Drivers 文件夹内附有官方原包和首次安装说明。"; };
            var dialog = new ContentDialog { XamlRoot = _root.XamlRoot, RequestedTheme = _root.ActualTheme, Title = "同步与主机播放", Content = Stack(sync, host, capture, output, correction, error, driver), PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            dialog.PrimaryButtonClick += (_, e) => { try { var updated = _options with { SyncEnabled = sync.IsChecked == true || host.IsChecked == true, HostPlayback = host.IsChecked == true, CaptureDeviceId = (capture.SelectedItem as AudioDeviceChoice)?.Id, OutputDeviceId = (output.SelectedItem as AudioDeviceChoice)?.Id, HostHardwareCorrectionMs = (int)correction.Value }; updated.Validate(); _options = updated; _volume.Configure(updated.OutputDeviceId); } catch (Exception ex) { e.Cancel = true; error.Text = ex.Message; } };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) { await Task.Run(_options.Save); _detail.Text = "同步设置已保存。"; }
        }
        catch (Exception ex) { _detail.Text = ex.Message; }
        finally { _settings.IsEnabled = true; }
    }
    async Task SaveDiagnosticsAsync()
    {
        try { var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"DormCast-诊断-{DateTime.Now:yyyyMMdd-HHmmss}" }; picker.FileTypeChoices.Add("JSON", new List<string> { ".json" }); WinRT.Interop.InitializeWithWindow.Initialize(picker, _window); var file = await picker.PickSaveFileAsync(); if (file == null) return; var json = await Task.Run(_host.Diagnostics); await Windows.Storage.FileIO.WriteTextAsync(file, json); _detail.Text = "诊断已保存。"; }
        catch (Exception ex) { _detail.Text = ex.Message; }
    }
}
