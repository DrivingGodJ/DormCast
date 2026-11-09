using System.Runtime.InteropServices;
using DormCast.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DormCast.Desktop;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--headless"))
        {
            AttachConsole(unchecked((uint)-1));
            try { return HeadlessRunner.RunAsync(args).GetAwaiter().GetResult(); }
            catch (Exception ex) { AppLog.Write("Headless failed", ex); Console.Error.WriteLine(ex.Message); return 1; }
        }
        using var single = new Mutex(true, args.Contains("--ui-preview") ? @"Local\DormCast.WinUIPreview" : @"Local\DormCast.Desktop", out var first);
        if (!first) { MessageBoxW(0, "宿舍之音已在运行，请从系统托盘打开主界面。", "宿舍之音", 0); return 0; }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        AppLog.Write("WinUI starting");
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }
    [DllImport("kernel32.dll")] static extern bool AttachConsole(uint processId);
    internal static void ShowError(string text) => MessageBoxW(0, text, "宿舍之音", 0x10);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(nint owner, string text, string title, uint type);
}

public sealed partial class App : Application
{
    MainWindow? _window;
    public App()
    {
        AppLog.Write("WinUI app created");
        UnhandledException += (_, e) => { AppLog.Write("WinUI exception", e.Exception); e.Handled = true; };
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args) { AppLog.Write("WinUI launching window"); try { _window = new MainWindow(); _window.Activate(); } catch (Exception ex) { AppLog.Write("WinUI launch failed", ex); Program.ShowError($"界面启动失败：{ex.Message}\n请重新完整安装程序。"); Exit(); } }
}
