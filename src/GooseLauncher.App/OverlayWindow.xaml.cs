using System.Diagnostics;
using System.Runtime.InteropServices;
using GooseLauncher.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace GooseLauncher.App;

public sealed partial class OverlayWindow : Window
{
    private const int DefaultWidth = 540;
    private const int DefaultHeight = 440;
    private const int MinimumWidth = 420;
    private const int MinimumHeight = 380;
    private const int MaximumWidth = 900;
    private const int MaximumHeight = 700;
    private const int ExtendedStyleIndex = -20;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const long ExtendedStyleAppWindow = 0x00040000L;

    private ActivationRequest _activation = ActivationRequest.Create(Environment.CurrentDirectory);
    private AcpClient? _client;
    private bool _running;
    private bool _allowClose;
    private bool _exitRequested;
    private uint _currentDpi = 96;
    private uint? _movePointerId;
    private NativePoint _moveStartCursor;
    private Windows.Graphics.PointInt32 _moveStartPosition;
    private TaskCompletionSource<AcpPermissionDecision>? _permissionCompletion;
    private AcpPermissionRequest? _permissionRequest;

    internal event Action? ExitRequested;

    public OverlayWindow()
    {
        InitializeComponent();
        ApplyStrings();

        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _currentDpi = GetDpiForWindow(windowHandle);
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
            _ = RequestExitAsync();
        };

        ApplyToolWindowStyle(windowHandle);
        UpdateRunButton();
    }

    internal void ShowActivation(ActivationRequest request)
    {
        if (_running)
        {
            PositionNearPointer(request.X, request.Y);
            AppWindow.Show();
            Activate();
            return;
        }

        var contextChanged = !string.Equals(_activation.Folder, request.Folder, StringComparison.OrdinalIgnoreCase) ||
            !_activation.Files.SequenceEqual(request.Files, StringComparer.OrdinalIgnoreCase);
        _activation = request;
        FolderPathTextBlock.Text = request.Folder;
        FilesPanel.Visibility = request.Files.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        FilesTextBlock.Text = request.Files.Count == 0
            ? string.Empty
            : string.Join(" · ", request.Files.Select(Path.GetFileName));

        if (contextChanged) PromptTextBox.Text = string.Empty;
        SetRunning(false);
        PositionNearPointer(request.X, request.Y);
        AppWindow.Show();
        Activate();
        DispatcherQueue.TryEnqueue(() => PromptTextBox.Focus(FocusState.Programmatic));
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();

    private async Task RunAsync()
    {
        var prompt = PromptTextBox.Text.Trim();
        if (_running || string.IsNullOrWhiteSpace(prompt)) return;

        var installation = GooseInstallation.Locate();
        if (installation is null)
        {
            SetError(Strings.Get(
                "找不到 Goose CLI。请安装 Goose Desktop，或设置 GOOSE_CLI_PATH。",
                "Goose CLI was not found. Install Goose Desktop or set GOOSE_CLI_PATH."));
            return;
        }

        if (_client is null)
        {
            _client = new AcpClient(installation);
            _client.UpdateReceived += update => DispatcherQueue.TryEnqueue(() => ApplyUpdate(update));
            _client.DiagnosticReceived += message => DispatcherQueue.TryEnqueue(() => SetDiagnostic(message));
            _client.PermissionRequested += RequestPermissionAsync;
        }

        SetRunning(true);
        OutputTextBox.Text = string.Empty;
        StatusTextBlock.Text = Strings.Get("正在连接 Goose…", "Connecting to Goose…");
        try
        {
            await _client.NewSessionAsync(_activation.Folder);
            StatusTextBlock.Text = Strings.Get("Goose 正在工作…", "Goose is working…");
            await _client.PromptAsync(prompt, _activation.Files);
            StatusTextBlock.Text = Strings.Get("完成", "Completed");
            SetRunning(false, completed: true);
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = Strings.Get("已取消", "Cancelled");
            SetRunning(false, completed: _client.SessionId is not null);
        }
        catch (Exception error)
        {
            SetRunning(false, completed: _client.SessionId is not null);
            SetError(error.Message);
        }
    }

    private void ApplyUpdate(AcpUpdate update)
    {
        if (update.Kind == AcpUpdateKind.Tool)
        {
            var status = string.IsNullOrWhiteSpace(update.Status) ? string.Empty : $" [{update.Status}]";
            StatusTextBlock.Text = $"{Strings.Get("工具", "Tool")}: {update.Text}{status}";
            if (!string.IsNullOrWhiteSpace(update.Text)) AppendOutput($"\n• {update.Text}{status}\n");
        }
        else if (update.Kind == AcpUpdateKind.Message && !string.IsNullOrEmpty(update.Text))
        {
            AppendOutput(update.Text);
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
            StatusTextBlock.Text = Strings.Get("等待权限确认", "Waiting for permission");
            Activate();
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
        StatusTextBlock.Text = Strings.Get("Goose 正在工作…", "Goose is working…");
    }

    private async void CancelRun_Click(object sender, RoutedEventArgs e)
    {
        CompletePermission(allow: false);
        if (_client is not null) await _client.CancelAsync();
        StatusTextBlock.Text = Strings.Get("正在取消…", "Cancelling…");
    }

    private void SetRunning(bool running, bool completed = false)
    {
        _running = running;
        PromptTextBox.Visibility = running || completed ? Visibility.Collapsed : Visibility.Visible;
        RunPanel.Visibility = running || completed ? Visibility.Visible : Visibility.Collapsed;
        RunButton.Visibility = running || completed ? Visibility.Collapsed : Visibility.Visible;
        CancelRunButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        OpenGooseButton.Visibility = completed ? Visibility.Visible : Visibility.Collapsed;
        ConfigureGooseButton.Visibility = Visibility.Collapsed;
        HintTextBlock.Visibility = running || completed ? Visibility.Collapsed : Visibility.Visible;
        UpdateRunButton();
    }

    private void AppendOutput(string text)
    {
        OutputTextBox.Text += text;
        OutputTextBox.SelectionStart = OutputTextBox.Text.Length;
    }

    internal void SetDiagnostic(string message)
    {
        if (_running && !string.IsNullOrWhiteSpace(message)) StatusTextBlock.Text = message;
    }

    private void SetError(string message)
    {
        _running = false;
        PromptTextBox.Visibility = Visibility.Collapsed;
        RunPanel.Visibility = Visibility.Visible;
        StatusTextBlock.Text = Strings.Get("错误", "Error");
        OutputTextBox.Text = message;
        RunButton.Visibility = Visibility.Collapsed;
        CancelRunButton.Visibility = Visibility.Collapsed;
        OpenGooseButton.Visibility = Visibility.Collapsed;
        ConfigureGooseButton.Visibility = Visibility.Visible;
        HintTextBlock.Visibility = Visibility.Collapsed;
    }

    internal void OpenGoose()
    {
        if (_client?.SessionId is { Length: > 0 } sessionId)
        {
            try
            {
                var clipboardData = new DataPackage();
                clipboardData.SetText(sessionId);
                Clipboard.SetContent(clipboardData);
            }
            catch { }
        }

        var uri = _client?.SessionId is { Length: > 0 } id
            ? $"goose://resume/{Uri.EscapeDataString(id)}"
            : "goose://new-session";
        StartGoose(uri);
    }

    private async void OpenGoose_Click(object sender, RoutedEventArgs e)
    {
        OpenGoose();
        await RequestExitAsync();
    }

    private async void ConfigureGoose_Click(object sender, RoutedEventArgs e)
    {
        StartGoose("goose://new-session");
        await RequestExitAsync();
    }

    private static void StartGoose(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            var installation = GooseInstallation.Locate();
            if (installation?.DesktopPath is not null)
                Process.Start(new ProcessStartInfo(installation.DesktopPath) { UseShellExecute = true });
        }
    }

    private async void Close_Click(object sender, RoutedEventArgs e) => await RequestExitAsync();

    private void PromptTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateRunButton();

    private void UpdateRunButton() =>
        RunButton.IsEnabled = !_running && !string.IsNullOrWhiteSpace(PromptTextBox.Text);

    private void PromptTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        _ = RequestExitAsync();
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

    private void ApplyStrings()
    {
        HeaderTitleText.Text = Strings.Get("在此位置使用 Goose", "Run Goose here");
        PromptTextBox.PlaceholderText = Strings.Get("告诉 Goose 要完成什么…", "Tell Goose what to do…");
        RunButton.Content = Strings.Get("运行", "Run");
        CloseButton.Content = Strings.Get("关闭", "Close");
        CancelRunButton.Content = Strings.Get("取消", "Cancel");
        OpenGooseButton.Content = Strings.Get("在 Goose 中打开", "Open in Goose");
        ConfigureGooseButton.Content = Strings.Get("在 Goose 中配置", "Configure in Goose");
        AllowButton.Content = Strings.Get("允许", "Allow");
        DenyButton.Content = Strings.Get("拒绝", "Deny");
        HintTextBlock.Text = Strings.Get(
            "Enter 运行 · Shift/Ctrl+Enter 换行 · Esc 关闭",
            "Enter to run · Shift/Ctrl+Enter for newline · Esc to close");
    }

    public async ValueTask DisposeSessionAsync()
    {
        _permissionCompletion?.TrySetResult(AcpPermissionDecision.Cancelled);
        _permissionCompletion = null;
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }

    private async Task RequestExitAsync()
    {
        if (_exitRequested) return;
        _exitRequested = true;
        CompletePermission(allow: false);
        if (_running && _client is not null)
        {
            try { await _client.CancelAsync(); }
            catch { }
        }
        ExitRequested?.Invoke();
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
        style = (style | ExtendedStyleToolWindow) & ~ExtendedStyleAppWindow;
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
