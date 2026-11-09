using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace DormCast.Desktop;

sealed class NativeTray : IDisposable
{
    const uint CallbackMessage = 0x8001;
    readonly nint _window, _idle = MakeIcon(false), _live = MakeIcon(true);
    readonly SubclassProc _procedure;
    readonly DispatcherQueue _queue;
    readonly Action _open;
    readonly Func<Task> _stop, _exit;
    readonly uint _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    bool _running;
    public NativeTray(nint window, DispatcherQueue queue, Action open, Func<Task> stop, Func<Task> exit)
    {
        _window = window; _queue = queue; _open = open; _stop = stop; _exit = exit;
        _procedure = WindowProcedure; SetWindowSubclass(window, _procedure, 1, 0); Add();
    }
    NotifyData Data() => new() { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = _window, Id = 1, Flags = 7, Callback = CallbackMessage, Icon = _running ? _live : _idle, Tip = _running ? "宿舍之音 · 正在广播" : "宿舍之音 · 未广播" };
    void Add() { var data = Data(); Shell_NotifyIconW(0, ref data); data.Version = 4; Shell_NotifyIconW(4, ref data); }
    public void SetBroadcasting(bool value) { if (_running == value) return; _running = value; var data = Data(); Shell_NotifyIconW(1, ref data); }
    nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == _taskbarCreated) Add();
        if (message == CallbackMessage)
        {
            var action = (uint)((long)lParam & 0xffff);
            if (action is 0x202 or 0x400 or 0x401) _queue.TryEnqueue(() => _open());
            else if (action is 0x205 or 0x7b) ShowMenu();
            return 0;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }
    void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, 0, 1, "打开主界面"); AppendMenuW(menu, _running ? 0u : 1u, 2, "停止广播");
            AppendMenuW(menu, 0x800, 0, null); AppendMenuW(menu, 0, 3, "退出程序");
            GetCursorPos(out var point); SetForegroundWindow(_window);
            var choice = TrackPopupMenu(menu, 0x102, point.X, point.Y, 0, _window, 0);
            if (choice == 1) _open();
            else if (choice == 2) _queue.TryEnqueue(async () => await _stop());
            else if (choice == 3) _queue.TryEnqueue(async () => await _exit());
            PostMessageW(_window, 0, 0, 0);
        }
        finally { DestroyMenu(menu); }
    }
    static nint MakeIcon(bool live)
    {
        var mask = new byte[32 * 4]; var colors = new byte[32 * 32 * 4];
        for (var y = 0; y < 32; y++) for (var x = 0; x < 32; x++)
        {
            var dx = x - 15.5; var dy = y - 15.5; var radius = Math.Sqrt(dx * dx + dy * dy);
            if (radius > 15) { mask[y * 4 + x / 8] |= (byte)(0x80 >> (x % 8)); continue; }
            var mark = radius < 2.5 || (Math.Abs(dx) > Math.Abs(dy) * 0.45 && ((radius > 7 && radius < 9) || (radius > 12 && radius < 14)));
            var index = (y * 32 + x) * 4;
            colors[index] = mark ? (byte)(live ? 127 : 155) : (byte)25;
            colors[index + 1] = mark ? (byte)(live ? 243 : 163) : (byte)31;
            colors[index + 2] = mark ? (byte)(live ? 201 : 155) : (byte)24;
            colors[index + 3] = 255;
        }
        return CreateIcon(0, 32, 32, 1, 32, mask, colors);
    }
    public void Dispose()
    {
        var data = Data(); Shell_NotifyIconW(2, ref data); RemoveWindowSubclass(_window, _procedure, 1); DestroyIcon(_idle); DestroyIcon(_live);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct NotifyData
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint Balloon;
    }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    delegate nint SubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint operation, ref NotifyData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string text);
    [DllImport("user32.dll")] static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] static extern nint CreateIcon(nint instance, int width, int height, byte planes, byte bits, byte[] mask, byte[] colors);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint icon);
}
