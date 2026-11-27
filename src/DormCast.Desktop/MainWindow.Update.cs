using System.Diagnostics;
using DormCast.Audio;
using DormCast.Hosting;
using DormCast.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DormCast.Desktop;

public sealed partial class MainWindow
{
    CancellationTokenSource? _startupUpdateCancel;
    bool _startupUpdateChecked;

    async Task CheckUpdateOnStartupAsync()
    {
        if (_exiting || _startupUpdateChecked) return;
        _startupUpdateChecked = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_exitCancel.Token);
        _startupUpdateCancel = cts;
        try
        {
            await Task.Delay(1500, cts.Token);
            using var client = new GitHubReleaseClient();
            var release = await client.CheckSilentlyAsync(CurrentVersion, cts.Token);
            if (release == null || _exiting || cts.IsCancellationRequested) return;
            _tray.ShowNotification(
                $"宿舍之音发现新版本 {release.Version}",
                $"当前版本 {CurrentVersion}，点击查看更新内容并安装。",
                () => { ShowMain(); _ = ShowDiscoveredUpdateAsync(release); });
            if (AppWindow.IsVisible && !_busy && !_dialogOpen && !_exiting && _root.XamlRoot != null)
                await ShowDiscoveredUpdateAsync(release);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write("Silent startup update check", ex); }
        finally { if (ReferenceEquals(_startupUpdateCancel, cts)) _startupUpdateCancel = null; }
    }

    async Task ShowDiscoveredUpdateAsync(ReleaseUpdate release)
    {
        if (_busy || _exiting || _dialogOpen || !_update.IsEnabled || _root.XamlRoot == null) return;
        _update.IsEnabled = false;
        try { await PromptAndInstallUpdateAsync(release); }
        catch (OperationCanceledException) { _detail.Text = "更新已取消，可稍后重试。"; }
        catch (Exception ex) { await MessageAsync("更新未完成", ex.Message + "\n官方发布页：" + GitHubReleaseClient.ReleasePage); }
        finally { _update.IsEnabled = true; _update.Content = "检查更新"; }
    }

    async Task CheckUpdateAsync()
    {
        if (_busy || _exiting || _dialogOpen || !_update.IsEnabled) return;
        try { _startupUpdateCancel?.Cancel(); } catch (ObjectDisposedException) { }
        _update.IsEnabled = false; _update.Content = "正在检查…";
        try
        {
            using var client = new GitHubReleaseClient();
            var current = CurrentVersion; var release = await client.CheckAsync(current, _exitCancel.Token);
            if (release == null) { await MessageAsync("已是最新版本", $"当前版本 {current}。GitHub 暂无更新的正式 Release。"); return; }
            await PromptAndInstallUpdateAsync(release);
        }
        catch (OperationCanceledException) { _detail.Text = "更新已取消，可稍后重试。"; }
        catch (Exception ex) { await MessageAsync("更新未完成", ex.Message + "\n官方发布页：" + GitHubReleaseClient.ReleasePage); }
        finally { _update.IsEnabled = true; _update.Content = "检查更新"; }
    }

    async Task PromptAndInstallUpdateAsync(ReleaseUpdate release)
    {
        if (_exiting || _dialogOpen || _root.XamlRoot == null) return;
        _dialogOpen = true;
        try
        {
            var confirm = new ContentDialog { XamlRoot = _root.XamlRoot, RequestedTheme = _root.ActualTheme, Title = $"发现新版本 {release.Version}", Content = new ScrollViewer { MaxHeight = 300, Content = Label($"当前版本 {CurrentVersion}\n\n{release.Notes}\n\n下载并校验后将停止广播、退出程序并打开安装向导。", 14) }, PrimaryButtonText = "下载并更新", CloseButtonText = "稍后", DefaultButton = ContentDialogButton.Primary };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            using var client = new GitHubReleaseClient();
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_exitCancel.Token);
            var bar = new ProgressBar { Minimum = 0, Maximum = 100 }; var label = Label("正在下载并校验安装包…", 14);
            var dialog = new ContentDialog { XamlRoot = _root.XamlRoot, RequestedTheme = _root.ActualTheme, Title = "下载更新", Content = Stack(label, bar), CloseButtonText = "取消" }; dialog.CloseButtonClick += (_, _) => cancel.Cancel();
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
        finally { _dialogOpen = false; }
    }

    async Task MessageAsync(string title, string message)
    {
        if (_exiting || _dialogOpen || _root.XamlRoot == null) return;
        _dialogOpen = true;
        try { await new ContentDialog { XamlRoot = _root.XamlRoot, RequestedTheme = _root.ActualTheme, Title = title, Content = Label(message, 14), CloseButtonText = "知道了" }.ShowAsync(); }
        finally { _dialogOpen = false; }
    }

    static bool TryLaunchBundledVirtualCableInstaller(out string error)
    {
        error = "";
        var folder = Path.Combine(AppContext.BaseDirectory, "Drivers");
        var zip = Path.Combine(folder, "VBCABLE_Driver_Pack46.zip");
        if (!File.Exists(zip))
        {
            if (Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
                error = "未找到内置驱动压缩包，已打开 Drivers 目录；也可前往 https://vb-audio.com/Cable/ 下载安装。";
                return false;
            }
            error = "未找到内置虚拟声卡安装包，请重新运行 DormCast 安装程序，或前往 https://vb-audio.com/Cable/ 下载安装 VB-CABLE。";
            return false;
        }
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "DormCast-VBCABLE");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, tempDir, true);
            var setup = Path.Combine(tempDir, "VBCABLE_Setup_x64.exe");
            if (!File.Exists(setup)) { error = "驱动压缩包中缺少 VBCABLE_Setup_x64.exe。"; return false; }
            Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, Verb = "runas", WorkingDirectory = tempDir });
            return true;
        }
        catch (Exception ex)
        {
            error = $"无法启动虚拟声卡安装程序：{ex.Message}";
            return false;
        }
    }

    async Task DriverGuideAsync()
    {
        if (_exiting || _dialogOpen || _root.XamlRoot == null) return;
        _dialogOpen = true;
        ContentDialogResult answer;
        try
        {
            var guide = new ContentDialog
            {
                XamlRoot = _root.XamlRoot,
                RequestedTheme = _root.ActualTheme,
                Title = "未检测到虚拟声卡（VB-CABLE）",
                Content = Label(
                    "普通用户建议安装虚拟声卡（VB-CABLE），由软件自动接管采集并同步到所有设备。\n\n" +
                    "请按以下任一方式完成安装：\n" +
                    "1. 点击下方「立即安装虚拟声卡」，在管理员授权后点击 Install Driver 完成安装；\n" +
                    "2. 或重新运行 DormCast 安装程序（会自动引导安装虚拟声卡）；\n" +
                    "3. 安装完成后按提示重启电脑，再打开软件或点击「重新检测」。\n\n" +
                    "如果你使用的是 Logitech MIXLINE / VoiceMeeter 等虚拟调音台，也可以直接关闭此提示并在「采集设备」中选择对应的输入端点（如 MIXLINE Stream）。\n" +
                    "若软件目录内不含驱动包，也可前往 https://vb-audio.com/Cable/ 下载官方 VB-CABLE。", 14),
                PrimaryButtonText = "立即安装虚拟声卡",
                SecondaryButtonText = "重新检测",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Primary
            };
            answer = await guide.ShowAsync();
        }
        finally { _dialogOpen = false; }

        if (answer == ContentDialogResult.Primary)
        {
            if (TryLaunchBundledVirtualCableInstaller(out var err))
                _detail.Text = "已启动 VB-CABLE 安装程序，安装完成后请按提示重启或点击「刷新网卡与设备」。";
            else
                _detail.Text = err;
        }
        else if (answer == ContentDialogResult.Secondary)
        {
            await RefreshNetworkAsync();
            if (_virtualCable != null) _detail.Text = $"已检测到虚拟声卡（{_virtualCable.Name}），现在可以开始广播。";
            else _detail.Text = "仍未检测到虚拟声卡，请确认已完成安装并重启电脑。";
        }
    }
}
