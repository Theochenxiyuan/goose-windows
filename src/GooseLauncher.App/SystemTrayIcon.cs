using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GooseLauncher.App;

internal sealed class SystemTrayIcon : IDisposable
{
    private const uint NotifyAdd = 0;
    private const uint NotifyModify = 1;
    private const uint NotifyDelete = 2;
    private const uint NotifySetVersion = 4;
    private const uint NotifyMessage = 1;
    private const uint NotifyIcon = 2;
    private const uint NotifyTip = 4;
    private const uint NotifyInfo = 16;
    private const uint NotifyIconVersion4 = 4;
    private const uint TrayCallbackMessage = 0x8001;
    private const uint WindowTimer = 0x0113;
    private const uint WindowNull = 0;
    private const nuint RetryTimerId = 1;
    private const uint MouseRightButtonUp = 0x0205;
    private const uint ContextMenu = 0x007B;
    private const uint MenuString = 0;
    private const uint MenuSeparator = 0x0800;
    private const uint TrackRightButton = 0x0002;
    private const uint TrackReturnCommand = 0x0100;
    private const uint TrackNoNotify = 0x0080;
    private const uint MenuOpenGoose = 1002;
    private const uint MenuSettings = 1003;
    private const uint MenuExit = 1004;

    private readonly WindowProcedure _windowProcedure;
    private readonly uint _iconId = unchecked((uint)Environment.ProcessId);
    private readonly string _windowClassName = $"GooseLauncher.Tray.{Environment.ProcessId}";
    private nint _windowHandle;
    private nint _iconHandle;
    private uint _taskbarCreatedMessage;
    private bool _iconAdded;
    private bool _busy;
    private bool _disposed;

    internal event Action? OpenGooseRequested;
    internal event Action? SettingsRequested;
    internal event Action? ExitRequested;

    internal SystemTrayIcon() => _windowProcedure = WindowProc;

    internal void Initialize()
    {
        if (_windowHandle != nint.Zero) return;

        var module = GetModuleHandle(null);
        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            Instance = module,
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
            ClassName = _windowClassName,
        };
        if (RegisterClassEx(ref windowClass) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to register the tray window class.");

        _windowHandle = CreateWindowEx(0, _windowClassName, "Goose Launcher Tray", 0, 0, 0, 0, 0,
            nint.Zero, nint.Zero, module, nint.Zero);
        if (_windowHandle == nint.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the tray message window.");

        _iconHandle = CreateGooseIcon(busy: false);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (!AddIcon()) SetTimer(_windowHandle, RetryTimerId, 1000, nint.Zero);
    }

    internal void SetBusy(bool busy)
    {
        if (_disposed || _busy == busy) return;
        _busy = busy;
        if (!_iconAdded) return;

        var replacement = CreateGooseIcon(busy);
        var data = CreateNotificationData();
        data.IconHandle = replacement;
        if (ShellNotifyIcon(NotifyModify, ref data))
        {
            if (_iconHandle != nint.Zero) DestroyIcon(_iconHandle);
            _iconHandle = replacement;
        }
        else if (replacement != nint.Zero)
        {
            DestroyIcon(replacement);
        }
    }

    internal void ShowNotification(string title, string message)
    {
        if (!_iconAdded) return;
        var data = CreateNotificationData();
        data.Flags |= NotifyInfo;
        data.InfoTitle = Limit(title, 63);
        data.Info = Limit(message, 255);
        data.InfoFlags = 1;
        ShellNotifyIcon(NotifyModify, ref data);
    }

    private bool AddIcon()
    {
        if (_windowHandle == nint.Zero) return false;
        var data = CreateNotificationData();
        if (!ShellNotifyIcon(NotifyAdd, ref data)) return false;
        _iconAdded = true;
        data.VersionOrTimeout = NotifyIconVersion4;
        ShellNotifyIcon(NotifySetVersion, ref data);
        return true;
    }

    private NotificationIconData CreateNotificationData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotificationIconData>(),
        WindowHandle = _windowHandle,
        Id = _iconId,
        Flags = NotifyMessage | NotifyIcon | NotifyTip,
        CallbackMessage = TrayCallbackMessage,
        IconHandle = _iconHandle,
        Tip = _busy
            ? Strings.Get("Goose Launcher · 正在运行", "Goose Launcher · Running")
            : Strings.Get("Goose Launcher · 就绪", "Goose Launcher · Ready"),
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (message == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
            {
                _iconAdded = false;
                if (!AddIcon()) SetTimer(_windowHandle, RetryTimerId, 1000, nint.Zero);
                return nint.Zero;
            }
            if (message == WindowTimer && (nuint)wParam == RetryTimerId)
            {
                if (AddIcon()) KillTimer(_windowHandle, RetryTimerId);
                return nint.Zero;
            }
            if (message == TrayCallbackMessage)
            {
                var mouseMessage = (uint)(lParam.ToInt64() & 0xFFFF);
                if (mouseMessage is MouseRightButtonUp or ContextMenu)
                    ShowContextMenu();
                return nint.Zero;
            }
        }
        catch { }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == nint.Zero) return;
        try
        {
            AppendMenu(menu, MenuString, MenuOpenGoose, Strings.Get("打开 Goose Desktop", "Open Goose Desktop"));
            AppendMenu(menu, MenuString, MenuSettings, Strings.Get("设置", "Settings"));
            AppendMenu(menu, MenuSeparator, 0, null);
            AppendMenu(menu, MenuString, MenuExit, Strings.Get("退出", "Exit"));
            SetMenuDefaultItem(menu, MenuOpenGoose, false);
            GetCursorPos(out var cursor);
            SetForegroundWindow(_windowHandle);
            var command = TrackPopupMenuEx(menu, TrackRightButton | TrackReturnCommand | TrackNoNotify,
                cursor.X, cursor.Y, _windowHandle, nint.Zero);
            PostMessage(_windowHandle, WindowNull, nint.Zero, nint.Zero);
            switch (command)
            {
                case MenuOpenGoose: OpenGooseRequested?.Invoke(); break;
                case MenuSettings: SettingsRequested?.Invoke(); break;
                case MenuExit: ExitRequested?.Invoke(); break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    private static nint CreateGooseIcon(bool busy, int size = 32)
    {
        var maskStride = ((size + 15) / 16) * 2;
        var andMask = new byte[maskStride * size];
        var color = new byte[size * size * 4];
        var background = busy ? ((byte)230, (byte)126, (byte)34) : ((byte)45, (byte)112, (byte)210);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var targetRow = size - 1 - y;
                var offset = (targetRow * size + x) * 4;
                var center = (size - 1) / 2d;
                var dx = x - center;
                var dy = y - center;
                var radius = size * 0.46;
                if (dx * dx + dy * dy > radius * radius)
                {
                    andMask[targetRow * maskStride + x / 8] |= (byte)(0x80 >> (x % 8));
                    continue;
                }

                var distance = Math.Sqrt(dx * dx + dy * dy);
                var ring = distance is >= 6.2 and <= 9.2 && !(dx > 4 && dy < -2);
                var bar = x is >= 15 and <= 24 && y is >= 15 and <= 18;
                var stem = x is >= 21 and <= 24 && y is >= 15 and <= 23;
                var white = ring || bar || stem;
                color[offset] = white ? (byte)255 : background.Item3;
                color[offset + 1] = white ? (byte)255 : background.Item2;
                color[offset + 2] = white ? (byte)255 : background.Item1;
                color[offset + 3] = 255;
            }
        }
        return CreateIcon(GetModuleHandle(null), size, size, 1, 32, andMask, color);
    }

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_windowHandle != nint.Zero) KillTimer(_windowHandle, RetryTimerId);
        if (_iconAdded)
        {
            var data = CreateNotificationData();
            ShellNotifyIcon(NotifyDelete, ref data);
            _iconAdded = false;
        }
        if (_windowHandle != nint.Zero) DestroyWindow(_windowHandle);
        if (_iconHandle != nint.Zero) DestroyIcon(_iconHandle);
        UnregisterClass(_windowClassName, GetModuleHandle(null));
        _windowHandle = nint.Zero;
        _iconHandle = nint.Zero;
    }

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        internal uint Size;
        internal uint Style;
        internal nint WindowProcedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint Background;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotificationIconData
    {
        internal uint Size;
        internal nint WindowHandle;
        internal uint Id;
        internal uint Flags;
        internal uint CallbackMessage;
        internal nint IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State;
        internal uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid ItemGuid;
        internal nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X; internal int Y; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? moduleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool UnregisterClass(string className, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreateIcon(nint instance, int width, int height, byte planes, byte bitsPerPixel, byte[] andBits, byte[] xorBits);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Shell_NotifyIconW")] private static extern bool ShellNotifyIcon(uint message, ref NotificationIconData data);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, uint item, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(nint menu, uint item, bool byPosition);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint window, nuint id);
}
