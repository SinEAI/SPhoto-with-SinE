using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace SPhoto.Services;

/// <summary>Library preparation status in the Windows notification area.</summary>
internal sealed class TrayStatus : IDisposable
{
    private const uint Callback = 0x8000 + 73;
    private readonly MainWindow _window;
    private readonly PhotoLibrary _library;
    private readonly SubclassProc _proc;
    private readonly DispatcherQueueTimer _timer;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly IntPtr _icon;
    private NotifyData _data;
    private bool _disposed;
    internal bool Visible { get; private set; }
    internal string Summary { get; private set; } = "";

    internal TrayStatus(MainWindow window, PhotoLibrary library)
    {
        _window = window;
        _library = library;
        _proc = WindowProc;
        _icon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", "SPhoto.ico"),
                          1, GetSystemMetrics(49), GetSystemMetrics(50), 0x10);
        _data = new NotifyData
        {
            Size = (uint)Marshal.SizeOf<NotifyData>(), Window = window.Hwnd, Id = 1,
            Flags = 1 | 2 | 4 | 0x80, Message = Callback,
            Icon = _icon != IntPtr.Zero ? _icon : LoadIcon(IntPtr.Zero, (IntPtr)32512), Tip = "SPhoto",
            Info = "", InfoTitle = "", Version = 4,
        };
        if (!SetWindowSubclass(window.Hwnd, _proc, 73, UIntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _timer = window.DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => Update();
        _timer.Start();
        Update();
    }

    internal void Update()
    {
        if (_disposed) return;
        var summary = _library.WorkSummary;
        if (Visible && Summary == summary) return;
        Summary = summary;
        _data.Tip = "SPhoto · " + summary;
        if (!Visible)
        {
            Visible = Shell_NotifyIcon(0, ref _data); // NIM_ADD
            if (Visible) Shell_NotifyIcon(4, ref _data); // NIM_SETVERSION
        }
        else if (!Shell_NotifyIcon(1, ref _data)) Visible = false;
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (message == _taskbarCreated)
        {
            Visible = false;
            Update();
        }
        else if (message == Callback)
        {
            // NOTIFYICON_VERSION_4 packs the notification in LOWORD(lParam).
            uint notification = (uint)(lParam.ToInt64() & 0xffff);
            if (notification is 0x400 or 0x401 or 0x203) _window.BringToFront();
            else if (notification == 0x7b) ShowMenu();
            return IntPtr.Zero;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, 1, UIntPtr.Zero, _library.WorkSummary);
            AppendMenu(menu, 0x800, UIntPtr.Zero, "");
            AppendMenu(menu, 0, (UIntPtr)1, "打开 SPhoto");
            AppendMenu(menu, 0, (UIntPtr)2, "退出");
            GetCursorPos(out var point);
            SetForegroundWindow(_window.Hwnd);
            uint command = TrackPopupMenu(menu, 0x102, point.X, point.Y, 0, _window.Hwnd, IntPtr.Zero);
            PostMessage(_window.Hwnd, 0, UIntPtr.Zero, IntPtr.Zero);
            if (command == 1) _window.BringToFront();
            if (command == 2) _window.DispatcherQueue.TryEnqueue(() => _window.Close());
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        if (Visible) Shell_NotifyIcon(2, ref _data);
        Visible = false;
        RemoveWindowSubclass(_data.Window, _proc, 73);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        internal uint Size;
        internal IntPtr Window;
        internal uint Id, Flags, Message;
        internal IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid Guid;
        internal IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint action, ref NotifyData data);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
}
