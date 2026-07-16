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
    private const uint WindowHotKey = 0x0312;
    private const uint WindowNull = 0;
    private const nuint RetryTimerId = 1;
    private const uint MouseRightButtonUp = 0x0205;
    private const uint MouseLeftButtonUp = 0x0202;
    private const uint MouseLeftButtonDoubleClick = 0x0203;
    private const uint ContextMenu = 0x007B;
    private const uint MenuString = 0;
    private const uint MenuSeparator = 0x0800;
    private const uint TrackRightButton = 0x0002;
    private const uint TrackReturnCommand = 0x0100;
    private const uint TrackNoNotify = 0x0080;
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x0010;
    private const uint LoadDefaultSize = 0x0040;
    private const uint MenuOpenGoose = 1002;
    private const uint MenuSettings = 1003;
    private const uint MenuExit = 1004;
    private const uint MenuOpenCli = 1005;
    private const uint MenuOpenLauncher = 1006;
    private const int QuickLauncherHotKeyId = 1;
    private const uint ModifierAlt = 0x0001;
    private const uint ModifierControl = 0x0002;
    private const uint ModifierShift = 0x0004;
    private const uint ModifierWindows = 0x0008;
    private const uint ModifierNoRepeat = 0x4000;

    private readonly WindowProcedure _windowProcedure;
    private readonly uint _iconId = unchecked((uint)Environment.ProcessId);
    private readonly string _windowClassName = $"GooseLauncher.Tray.{Environment.ProcessId}";
    private nint _windowHandle;
    private nint _iconHandle;
    private uint _taskbarCreatedMessage;
    private bool _iconAdded;
    private bool _busy;
    private bool _hotKeyRegistered;
    private long _lastLauncherInvocation;
    private bool _disposed;

    internal event Action? OpenGooseRequested;
    internal event Action? OpenLauncherRequested;
    internal event Action? OpenCliRequested;
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

        _windowHandle = CreateWindowEx(0, _windowClassName, "Goose Tray", 0, 0, 0, 0, 0,
            nint.Zero, nint.Zero, module, nint.Zero);
        if (_windowHandle == nint.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the tray message window.");

        _iconHandle = LoadImage(
            nint.Zero,
            GooseBranding.IconPath,
            ImageIcon,
            0,
            0,
            LoadFromFile | LoadDefaultSize);
        if (_iconHandle == nint.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to load the Goose tray icon.");
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (!AddIcon()) SetTimer(_windowHandle, RetryTimerId, 1000, nint.Zero);
    }

    internal void SetBusy(bool busy)
    {
        if (_disposed || _busy == busy) return;
        _busy = busy;
        if (!_iconAdded) return;

        var data = CreateNotificationData();
        ShellNotifyIcon(NotifyModify, ref data);
    }

    internal bool SetQuickLauncherShortcut(string shortcut)
    {
        if (_windowHandle == nint.Zero) return false;
        if (_hotKeyRegistered)
        {
            UnregisterHotKey(_windowHandle, QuickLauncherHotKeyId);
            _hotKeyRegistered = false;
        }
        if (!TryParseShortcut(shortcut, out var modifiers, out var key)) return false;
        _hotKeyRegistered = RegisterHotKey(
            _windowHandle,
            QuickLauncherHotKeyId,
            modifiers | ModifierNoRepeat,
            key);
        return _hotKeyRegistered;
    }

    internal static bool IsSupportedShortcut(string shortcut) =>
        TryParseShortcut(shortcut, out _, out _);

    private static bool TryParseShortcut(string shortcut, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        if (string.IsNullOrWhiteSpace(shortcut)) return false;
        foreach (var part in shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                case "COMMANDORCONTROL": modifiers |= ModifierControl; break;
                case "ALT": modifiers |= ModifierAlt; break;
                case "SHIFT": modifiers |= ModifierShift; break;
                case "WIN":
                case "WINDOWS":
                case "SUPER": modifiers |= ModifierWindows; break;
                default:
                    if (key != 0) return false;
                    if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
                        key = char.ToUpperInvariant(part[0]);
                    else if (part.Length > 1 && part[0] is 'F' or 'f' &&
                        int.TryParse(part[1..], out var functionKey) && functionKey is >= 1 and <= 24)
                        key = (uint)(0x70 + functionKey - 1);
                    else
                        return false;
                    break;
            }
        }
        return modifiers != 0 && key != 0;
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
            ? Strings.Get("Goose · 正在打开", "Goose · Opening")
            : Strings.Get("Goose · 就绪", "Goose · Ready"),
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
                else if (mouseMessage is MouseLeftButtonUp or MouseLeftButtonDoubleClick)
                    RaiseOpenLauncher();
                return nint.Zero;
            }
            if (message == WindowHotKey && wParam.ToInt32() == QuickLauncherHotKeyId)
            {
                RaiseOpenLauncher();
                return nint.Zero;
            }
        }
        catch { }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private void RaiseOpenLauncher()
    {
        var now = Environment.TickCount64;
        if (now - _lastLauncherInvocation < 300) return;
        _lastLauncherInvocation = now;
        OpenLauncherRequested?.Invoke();
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == nint.Zero) return;
        try
        {
            AppendMenu(menu, MenuString, MenuOpenLauncher, Strings.Get("新建 Goose 任务", "New Goose task"));
            AppendMenu(menu, MenuSeparator, 0, null);
            AppendMenu(menu, MenuString, MenuOpenGoose, Strings.Get("打开 Goose Desktop", "Open Goose Desktop"));
            AppendMenu(menu, MenuString, MenuOpenCli, Strings.Get("打开 Goose CLI", "Open Goose CLI"));
            AppendMenu(menu, MenuString, MenuSettings, Strings.Get("设置", "Settings"));
            AppendMenu(menu, MenuSeparator, 0, null);
            AppendMenu(menu, MenuString, MenuExit, Strings.Get("退出 Goose", "Exit Goose"));
            SetMenuDefaultItem(menu, MenuOpenLauncher, false);
            GetCursorPos(out var cursor);
            SetForegroundWindow(_windowHandle);
            var command = TrackPopupMenuEx(menu, TrackRightButton | TrackReturnCommand | TrackNoNotify,
                cursor.X, cursor.Y, _windowHandle, nint.Zero);
            PostMessage(_windowHandle, WindowNull, nint.Zero, nint.Zero);
            switch (command)
            {
                case MenuOpenLauncher: OpenLauncherRequested?.Invoke(); break;
                case MenuOpenGoose: OpenGooseRequested?.Invoke(); break;
                case MenuOpenCli: OpenCliRequested?.Invoke(); break;
                case MenuSettings: SettingsRequested?.Invoke(); break;
                case MenuExit: ExitRequested?.Invoke(); break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hotKeyRegistered) UnregisterHotKey(_windowHandle, QuickLauncherHotKeyId);
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadImage(nint instance, string name, uint type, int desiredWidth, int desiredHeight, uint load);
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
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
