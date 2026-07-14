using System.Runtime.InteropServices;
using GooseLauncher.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace GooseLauncher.App;

public sealed partial class OverlayWindow : Window
{
    private const int DefaultWidth = 540;
    private const int DefaultHeight = 360;
    private const int MinimumWidth = 420;
    private const int MinimumHeight = 300;
    private const int MaximumWidth = 900;
    private const int MaximumHeight = 520;
    private const int ExtendedStyleIndex = -20;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const long ExtendedStyleAppWindow = 0x00040000L;
    private const long ExtendedStyleTopmost = 0x00000008L;
    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoMove = 0x0002;
    private const uint SetWindowPositionShowWindow = 0x0040;
    private static readonly nint WindowTopmost = new(-1);

    private readonly nint _windowHandle;
    private ActivationRequest _activation = ActivationRequest.Create(Environment.CurrentDirectory);
    private AcpClient? _client;
    private bool _running;
    private bool _handedOff;
    private bool _hasActivationContext;
    private bool _resetClientWhenIdle;
    private bool _allowClose;
    private uint _currentDpi = 96;
    private uint? _movePointerId;
    private NativePoint _moveStartCursor;
    private Windows.Graphics.PointInt32 _moveStartPosition;
    private TaskCompletionSource<AcpPermissionDecision>? _permissionCompletion;
    private AcpPermissionRequest? _permissionRequest;

    internal string CurrentFolderForLaunch => _hasActivationContext
        ? _activation.Folder
        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public OverlayWindow()
    {
        InitializeComponent();
        ApplyStrings();

        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _currentDpi = GetDpiForWindow(_windowHandle);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Scale(DefaultWidth, _currentDpi), Scale(DefaultHeight, _currentDpi)));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        AppWindow.Closing += (sender, args) =>
        {
            _ = sender;
            if (_allowClose) return;
            args.Cancel = true;
            _ = HideOverlayAsync();
        };

        ApplyToolWindowStyle(_windowHandle);
        UpdateRunButton();
    }

    internal void ShowActivation(ActivationRequest request)
    {
        if (_running)
        {
            App.Instance?.ShowNotification(
                Strings.Get("Goose 任务正在运行", "Goose task is running"),
                Strings.Get("当前任务完成后即可新建任务。", "A new task can be started after the current task finishes."));
            return;
        }

        var contextChanged = !string.Equals(_activation.Folder, request.Folder, StringComparison.OrdinalIgnoreCase) ||
            !_activation.Files.SequenceEqual(request.Files, StringComparer.OrdinalIgnoreCase);
        _activation = request;
        _hasActivationContext = true;
        FolderPathTextBlock.Text = request.Folder;
        FilesPanel.Visibility = request.Files.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        FilesTextBlock.Text = request.Files.Count == 0
            ? string.Empty
            : string.Join(" · ", request.Files.Select(Path.GetFileName));

        if (contextChanged) PromptTextBox.Text = string.Empty;
        SetSubmitting(false);
        PositionNearPointer(request.X, request.Y);
        ShowTopmost();
        DispatcherQueue.TryEnqueue(() => PromptTextBox.Focus(FocusState.Programmatic));
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();

    private async Task RunAsync()
    {
        var prompt = PromptTextBox.Text.Trim();
        if (_running || string.IsNullOrWhiteSpace(prompt)) return;

        var settings = CompanionSettingsStore.Load();
        var installation = GooseInstallation.Locate(settings.CliPath, settings.DesktopPath);
        if (installation is null)
        {
            SetError(Strings.Get(
                "找不到 Goose CLI。请在 Goose Launcher 设置中检查路径。",
                "Goose CLI was not found. Check the path in Goose Launcher settings."));
            return;
        }

        if (settings.RunTarget == GooseRunTarget.Desktop && installation.DesktopPath is null)
        {
            SetError(Strings.Get(
                "找不到 Goose Desktop。请在 Goose Launcher 设置中检查路径。",
                "Goose Desktop was not found. Check the path in Goose Launcher settings."));
            return;
        }

        ErrorInfoBar.IsOpen = false;
        SetSubmitting(true);

        if (settings.RunTarget == GooseRunTarget.Terminal)
        {
            try
            {
                GooseProcessLauncher.OpenTerminal(
                    installation,
                    _activation.Folder,
                    AcpClient.BuildPromptText(prompt, _activation.Files));
                AppWindow.Hide();
                await CompleteRunAsync();
            }
            catch (Exception error)
            {
                SetError(error.Message);
            }
            return;
        }

        if (_client is null)
        {
            _client = new AcpClient(installation);
            _client.DiagnosticReceived += message => DispatcherQueue.TryEnqueue(() => SetDiagnostic(message));
            _client.PermissionRequested += RequestPermissionAsync;
        }

        try
        {
            var sessionId = await _client.NewSessionAsync(_activation.Folder);
            var promptTask = await _client.StartPromptAsync(prompt, _activation.Files);

            GooseProcessLauncher.OpenDesktop(installation, $"goose://resume/{Uri.EscapeDataString(sessionId)}");
            _handedOff = true;
            AppWindow.Hide();

            await promptTask;
            await CompleteRunAsync();
        }
        catch (OperationCanceledException)
        {
            if (_handedOff) await CompleteRunAsync();
            else SetSubmitting(false);
        }
        catch (Exception error)
        {
            if (_handedOff)
            {
                App.Instance?.ShowNotification(
                    Strings.Get("Goose 任务失败", "Goose task failed"),
                    error.Message);
                await CompleteRunAsync();
            }
            else
            {
                if (_client is not null)
                {
                    try { await _client.CancelAsync(); }
                    catch { }
                }
                SetError(error.Message);
            }
        }
    }

    private async Task CompleteRunAsync()
    {
        _running = false;
        _handedOff = false;
        PromptTextBox.Text = string.Empty;
        SetSubmitting(false);
        if (_resetClientWhenIdle)
        {
            _resetClientWhenIdle = false;
            await DisposeClientAsync();
        }
    }

    private Task<AcpPermissionDecision> RequestPermissionAsync(AcpPermissionRequest request)
    {
        var completion = new TaskCompletionSource<AcpPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _permissionRequest = request;
            _permissionCompletion = completion;
            PermissionTextBlock.Text = string.IsNullOrWhiteSpace(request.Title)
                ? Strings.Get("Goose 请求运行工具", "Goose requests permission to run a tool")
                : request.Title;
            PermissionPanel.Visibility = Visibility.Visible;
            ShowTopmost();
            AllowButton.Focus(FocusState.Programmatic);
        }))
        {
            completion.TrySetResult(AcpPermissionDecision.Cancelled);
        }

        return completion.Task;
    }

    private void Allow_Click(object sender, RoutedEventArgs e) => CompletePermission(allow: true);

    private void Deny_Click(object sender, RoutedEventArgs e) => CompletePermission(allow: false);

    private void CompletePermission(bool allow)
    {
        if (_permissionCompletion is null) return;

        var options = _permissionRequest?.Options ?? [];
        var option = allow
            ? options.FirstOrDefault(value => value.Kind.Contains("allow", StringComparison.OrdinalIgnoreCase)) ?? options.FirstOrDefault()
            : options.FirstOrDefault(value =>
                value.Kind.Contains("reject", StringComparison.OrdinalIgnoreCase) ||
                value.Kind.Contains("deny", StringComparison.OrdinalIgnoreCase));

        _permissionCompletion.TrySetResult(option is null
            ? AcpPermissionDecision.Cancelled
            : new AcpPermissionDecision(option.Id));
        _permissionCompletion = null;
        _permissionRequest = null;
        PermissionPanel.Visibility = Visibility.Collapsed;
        if (_handedOff) AppWindow.Hide();
    }

    private void SetSubmitting(bool submitting)
    {
        _running = submitting;
        PromptTextBox.IsEnabled = !submitting;
        CloseButton.IsEnabled = !submitting;
        OpenTargetButton.IsEnabled = !submitting;
        OpenSettingsButton.Visibility = Visibility.Collapsed;
        App.Instance?.SetBusy(submitting);
        RunButton.Content = submitting
            ? Strings.Get("正在打开 Goose…", "Opening Goose…")
            : Strings.Get("运行", "Run");
        HintTextBlock.Text = submitting
            ? Strings.Get("正在创建 Goose 会话…", "Creating a Goose session…")
            : GetDefaultHint();
        UpdateRunButton();
    }

    internal void SetDiagnostic(string message)
    {
        if (_running && !_handedOff && !string.IsNullOrWhiteSpace(message)) HintTextBlock.Text = message;
    }

    private void SetError(string message)
    {
        SetSubmitting(false);
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
        OpenSettingsButton.Visibility = Visibility.Visible;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        AppWindow.Hide();
        App.Instance?.ShowSettings(AppWindow);
    }

    private void OpenTarget_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = CompanionSettingsStore.Load();
            var installation = GooseInstallation.Locate(settings.CliPath, settings.DesktopPath)
                ?? throw new FileNotFoundException(Strings.Get("Goose CLI 未找到。", "Goose CLI was not found."));
            if (settings.RunTarget == GooseRunTarget.Desktop)
            {
                if (installation.DesktopPath is null)
                    throw new FileNotFoundException(Strings.Get("Goose Desktop 未找到。", "Goose Desktop was not found."));
                GooseProcessLauncher.OpenDesktop(installation, "goose://new-session");
            }
            else
            {
                GooseProcessLauncher.OpenTerminalSession(installation, CurrentFolderForLaunch);
            }
            AppWindow.Hide();
        }
        catch (Exception error)
        {
            SetError(error.Message);
        }
    }

    private async void Close_Click(object sender, RoutedEventArgs e) => await HideOverlayAsync();

    private void PromptTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateRunButton();

    private void UpdateRunButton() =>
        RunButton.IsEnabled = !_running && !string.IsNullOrWhiteSpace(PromptTextBox.Text);

    private void PromptTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        _ = HideOverlayAsync();
    }

    private void PromptTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || IsControlDown() || IsShiftDown()) return;
        e.Handled = true;
        if (RunButton.IsEnabled) _ = RunAsync();
    }

    private static bool IsControlDown() =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private static bool IsShiftDown() =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var position = AppWindow.Position;
        var display = DisplayArea.GetFromPoint(position, DisplayAreaFallback.Nearest);
        var workArea = display.WorkArea;
        var maximumWidth = Math.Min(Scale(MaximumWidth, _currentDpi), Math.Max(1, workArea.X + workArea.Width - position.X));
        var maximumHeight = Math.Min(Scale(MaximumHeight, _currentDpi), Math.Max(1, workArea.Y + workArea.Height - position.Y));
        var minimumWidth = Math.Min(Scale(MinimumWidth, _currentDpi), maximumWidth);
        var minimumHeight = Math.Min(Scale(MinimumHeight, _currentDpi), maximumHeight);
        var width = Math.Clamp(AppWindow.Size.Width + (int)Math.Round(e.HorizontalChange), minimumWidth, maximumWidth);
        var height = Math.Clamp(AppWindow.Size.Height + (int)Math.Round(e.VerticalChange), minimumHeight, maximumHeight);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
    }

    private void MoveRegion_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed || !GetCursorPos(out _moveStartCursor)) return;
        _movePointerId = e.Pointer.PointerId;
        _moveStartPosition = AppWindow.Position;
        MoveRegion.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void MoveRegion_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_movePointerId != e.Pointer.PointerId || !GetCursorPos(out var cursor)) return;

        var requested = new Windows.Graphics.PointInt32(
            _moveStartPosition.X + cursor.X - _moveStartCursor.X,
            _moveStartPosition.Y + cursor.Y - _moveStartCursor.Y);
        var display = DisplayArea.GetFromPoint(requested, DisplayAreaFallback.Nearest);
        var workArea = display.WorkArea;
        var maximumLeft = Math.Max(workArea.X, workArea.X + workArea.Width - AppWindow.Size.Width);
        var maximumTop = Math.Max(workArea.Y, workArea.Y + workArea.Height - AppWindow.Size.Height);
        AppWindow.Move(new Windows.Graphics.PointInt32(
            Math.Clamp(requested.X, workArea.X, maximumLeft),
            Math.Clamp(requested.Y, workArea.Y, maximumTop)));
        e.Handled = true;
    }

    private void MoveRegion_PointerReleased(object sender, PointerRoutedEventArgs e) => EndMove(e.Pointer);

    private void MoveRegion_PointerCanceled(object sender, PointerRoutedEventArgs e) => EndMove(e.Pointer);

    private void MoveRegion_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_movePointerId == e.Pointer.PointerId) _movePointerId = null;
    }

    private void EndMove(Pointer pointer)
    {
        if (_movePointerId != pointer.PointerId) return;
        _movePointerId = null;
        MoveRegion.ReleasePointerCapture(pointer);
    }

    private void PositionNearPointer(int? x, int? y)
    {
        var point = x is int cursorX && y is int cursorY
            ? new Windows.Graphics.PointInt32(cursorX, cursorY)
            : GetCursorPos(out var cursor)
                ? new Windows.Graphics.PointInt32(cursor.X, cursor.Y)
                : new Windows.Graphics.PointInt32(0, 0);

        var display = DisplayArea.GetFromPoint(point, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        _currentDpi = GetDpiForPoint(point);
        var size = AppWindow.Size;
        var offset = Scale(18, _currentDpi);
        var maximumLeft = Math.Max(workArea.X, workArea.X + workArea.Width - size.Width);
        var maximumTop = Math.Max(workArea.Y, workArea.Y + workArea.Height - size.Height);
        AppWindow.Move(new Windows.Graphics.PointInt32(
            Math.Clamp(point.X + offset, workArea.X, maximumLeft),
            Math.Clamp(point.Y + offset, workArea.Y, maximumTop)));
    }

    private void ShowTopmost()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = true;
        AppWindow.Show();
        Activate();
        SetWindowPos(
            _windowHandle,
            WindowTopmost,
            0,
            0,
            0,
            0,
            SetWindowPositionNoSize | SetWindowPositionNoMove | SetWindowPositionShowWindow);
    }

    private void ApplyStrings()
    {
        HeaderTitleText.Text = Strings.Get("在此位置使用 Goose", "Run Goose here");
        PromptTextBox.PlaceholderText = Strings.Get("告诉 Goose 要完成什么…", "Tell Goose what to do…");
        RunButton.Content = Strings.Get("运行", "Run");
        CloseButton.Content = Strings.Get("关闭", "Close");
        OpenSettingsButton.Content = Strings.Get("设置", "Settings");
        AllowButton.Content = Strings.Get("允许", "Allow");
        DenyButton.Content = Strings.Get("拒绝", "Deny");
        HintTextBlock.Text = GetDefaultHint();
        UpdateOpenTargetButton();
    }

    private void UpdateOpenTargetButton()
    {
        OpenTargetButton.Content = CompanionSettingsStore.Load().RunTarget == GooseRunTarget.Terminal
            ? Strings.Get("打开 Goose CLI", "Open Goose CLI")
            : Strings.Get("打开 Goose Desktop", "Open Goose Desktop");
    }

    private static string GetDefaultHint() =>
        Strings.Get(
            "Enter 运行 · Shift/Ctrl+Enter 换行 · Esc 关闭",
            "Enter to run · Shift/Ctrl+Enter for newline · Esc to close");

    public async ValueTask DisposeSessionAsync()
    {
        _permissionCompletion?.TrySetResult(AcpPermissionDecision.Cancelled);
        _permissionCompletion = null;
        if (_running && _client is not null)
        {
            try { await _client.CancelAsync(); }
            catch { }
        }
        await DisposeClientAsync();
    }

    private async Task DisposeClientAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }

    internal async Task OnSettingsChangedAsync()
    {
        UpdateOpenTargetButton();
        if (_running)
        {
            _resetClientWhenIdle = true;
            return;
        }
        await DisposeClientAsync();
    }

    private async Task HideOverlayAsync()
    {
        CompletePermission(allow: false);
        if (_running && !_handedOff && _client is not null)
        {
            try { await _client.CancelAsync(); }
            catch { }
            _running = false;
            SetSubmitting(false);
        }
        AppWindow.Hide();
    }

    internal void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private static int Scale(double value, uint dpi) =>
        (int)Math.Round(value * (dpi == 0 ? 1d : dpi / 96d));

    private static uint GetDpiForPoint(Windows.Graphics.PointInt32 point)
    {
        var monitor = MonitorFromPoint(new NativePoint(point.X, point.Y), 2);
        return monitor != nint.Zero && GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX : 96u;
    }

    private static void ApplyToolWindowStyle(nint windowHandle)
    {
        var style = GetWindowLongPtr(windowHandle, ExtendedStyleIndex).ToInt64();
        style = (style | ExtendedStyleToolWindow | ExtendedStyleTopmost) & ~ExtendedStyleAppWindow;
        SetWindowLongPtr(windowHandle, ExtendedStyleIndex, new nint(style));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        internal readonly int X = x;
        internal readonly int Y = y;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
