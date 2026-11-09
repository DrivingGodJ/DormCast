using System.Diagnostics;
using DormCast.Audio;
using DormCast.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DormCast.Desktop;

public sealed partial class MainWindow
{
    async Task CheckUpdateAsync()
    {
        if (_busy || _exiting || !_update.IsEnabled) return; _update.IsEnabled = false; _update.Content = "正在检查…";
        try
        {
            using var client = new GitHubReleaseClient();
            var current = new Version(0, 5, 3); var release = await client.CheckAsync(current);
            if (release == null) { await MessageAsync("已是最新版本", $"当前版本 {current}。GitHub 暂无更新的正式 Release。"); return; }
            var confirm = new ContentDialog { XamlRoot = _root.XamlRoot, Title = $"发现新版本 {release.Version}", Content = new ScrollViewer { MaxHeight = 300, Content = Label($"当前版本 {current}\n\n{release.Notes}\n\n下载并校验后将停止广播、退出程序并打开安装向导。", 14) }, PrimaryButtonText = "下载并更新", CloseButtonText = "稍后", DefaultButton = ContentDialogButton.Primary };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            using var cancel = new CancellationTokenSource(); var bar = new ProgressBar { Minimum = 0, Maximum = 100 }; var label = Label("正在下载并校验安装包…", 14);
            var dialog = new ContentDialog { XamlRoot = _root.XamlRoot, Title = "下载更新", Content = Stack(label, bar), CloseButtonText = "取消" }; dialog.CloseButtonClick += (_, _) => cancel.Cancel();
            var shown = dialog.ShowAsync(); string installer;
            try { installer = await client.DownloadAsync(release, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DormCast", "updates"), new Progress<double>(p => { bar.Value = p * 100; label.Text = $"下载 {p:P0} · 完成后验证 SHA-256"; }), cancel.Token); }
            finally { dialog.Hide(); await shown; }
            // An external Windows helper waits until this process releases all audio and file handles.
            var helper = Path.Combine(AppContext.BaseDirectory, "Updates", "Install-Update.ps1"); if (!File.Exists(helper)) throw new FileNotFoundException("更新助手缺失，请重新下载安装包。", helper);
            var launch = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", helper, "-ParentId", Environment.ProcessId.ToString(), "-Installer", installer, "-Sha256", release.Sha256 }) launch.ArgumentList.Add(argument);
            using var process = Process.Start(launch) ?? throw new IOException("无法启动更新助手。");
            await ExitAsync();
        }
        catch (OperationCanceledException) { _detail.Text = "更新已取消，可稍后重试。"; }
        catch (Exception ex) { await MessageAsync("更新未完成", ex.Message + "\n官方发布页：" + GitHubReleaseClient.ReleasePage); }
        finally { _update.IsEnabled = true; _update.Content = "检查更新"; }
    }
    async Task MessageAsync(string title, string message) => await new ContentDialog { XamlRoot = _root.XamlRoot, Title = title, Content = Label(message, 14), CloseButtonText = "知道了" }.ShowAsync();
    async Task DriverGuideAsync()
    {
        var guide = new ContentDialog { XamlRoot = _root.XamlRoot, Title = "首次使用：安装虚拟声卡", Content = Label("1. 打开附带的驱动文件夹，解压 VBCABLE_Driver_Pack46.zip。\n\n2. 右键 VBCABLE_Setup_x64.exe，以管理员身份运行，点击 Install Driver。\n\n3. 按官方提示重启电脑，再打开宿舍之音。软件会选择 CABLE Input 采集、实际扬声器播放。\n\n官方驱动原包保持完整，需要你手动安装一次。", 15), PrimaryButtonText = "打开驱动文件夹", SecondaryButtonText = "重新检测", CloseButtonText = "稍后" };
        var answer = await guide.ShowAsync();
        if (answer == ContentDialogResult.Primary) Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Drivers")) { UseShellExecute = true });
        if (answer == ContentDialogResult.Secondary)
        {
            var devices = await Task.Run(AudioDevices.RenderDevices); var source = devices.FirstOrDefault(d => d.IsVirtual); var output = devices.FirstOrDefault(d => !d.IsVirtual && d.Name.Contains("Realtek", StringComparison.OrdinalIgnoreCase)) ?? devices.FirstOrDefault(d => !d.IsVirtual);
            if (source != null && output != null) { _options = _options with { HostPlayback = true, CaptureDeviceId = source.Id, OutputDeviceId = output.Id }; _volume.Configure(output.Id); await Task.Run(_options.Save); _detail.Text = "驱动已识别，可以开始广播。"; }
            else _detail.Text = "尚未识别到虚拟声卡，请安装驱动并重启后重试。";
        }
    }
}
