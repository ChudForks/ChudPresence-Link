using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ChudPresence.Link.Windows;

// Shell notification icon and a small native menu; the application itself is entirely WinUI.
internal sealed class NativeTray : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private readonly Action open;
    private readonly Action pairBrowser;
    private readonly Action pairPhone;
    private readonly Action exit;
    private readonly WindowProcedure procedure;
    private readonly string className = "ChudPresence.Link.Tray." + Guid.NewGuid().ToString("N");
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly nint instance = GetModuleHandle(null);
    private nint handle;
    private nint icon;
    private bool ownsIcon;
    private NotifyIconData data;

    public NativeTray(Action open, Action pairBrowser, Action pairPhone, Action exit)
    {
        this.open = open;
        this.pairBrowser = pairBrowser;
        this.pairPhone = pairPhone;
        this.exit = exit;
        procedure = ProcessMessage;
        var windowClass = new WindowClass { Procedure = procedure, Instance = instance, ClassName = className };
        if (RegisterClass(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        handle = CreateWindowEx(0x80, className, "ChudPresence Link tray", 0, 0, 0, 0, 0, 0, 0, instance, 0);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "Link.ico"), 1, 0, 0, 0x10 | 0x40);
        ownsIcon = icon != 0;
        if (!ownsIcon) icon = LoadIcon(0, 32512);
        data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = handle, Id = 1,
            Flags = 1 | 2 | 4, CallbackMessage = CallbackMessage, Icon = icon,
            Tip = "ChudPresence Link", Info = "", InfoTitle = ""
        };
        ShellNotifyIcon(0, ref data);
    }

    private nint ProcessMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == taskbarCreated)
        {
            ShellNotifyIcon(0, ref data);
            return 0;
        }
        if (message == CallbackMessage)
        {
            switch ((uint)lParam)
            {
                case 0x202: // WM_LBUTTONUP
                case 0x203: // WM_LBUTTONDBLCLK
                    open();
                    break;
                case 0x205: // WM_RBUTTONUP
                case 0x7B: // WM_CONTEXTMENU
                    ShowMenu();
                    break;
            }
            return 0;
        }
        if (message == 0x11) return 1; // WM_QUERYENDSESSION
        if (message == 0x16 && wParam != 0) exit(); // WM_ENDSESSION
        return DefWindowProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        uint command;
        try
        {
            AppendMenu(menu, 0, 1, "Open ChudPresence Link");
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, 2, "Pair browser");
            AppendMenu(menu, 0, 3, "Pair phone");
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, 4, "Exit Link");
            SetMenuDefaultItem(menu, 1, false);
            GetCursorPos(out var point);
            SetForegroundWindow(handle);
            command = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, handle, 0);
            PostMessage(handle, 0, 0, 0);
        }
        finally { DestroyMenu(menu); }
        switch (command)
        {
            case 1: open(); break;
            case 2: pairBrowser(); break;
            case 3: pairPhone(); break;
            case 4: exit(); break;
        }
    }

    public void Dispose()
    {
        if (handle == 0) return;
        ShellNotifyIcon(2, ref data);
        DestroyWindow(handle);
        handle = 0;
        if (ownsIcon) DestroyIcon(icon);
        icon = 0;
        UnregisterClass(className, instance);
        GC.KeepAlive(procedure);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public WindowProcedure Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass windowClass);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string className, nint instance);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] private static extern bool ShellNotifyIcon(uint command, ref NotifyIconData data);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? label);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(nint menu, uint item, bool byPosition);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint owner, nint rectangle);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
}
