using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using GooseLauncher.Core;

namespace GooseLauncher.App;

public partial class OverlayWindow : Window
{
    private ActivationRequest _activation = ActivationRequest.Create(Environment.CurrentDirectory);
    private AcpClient? _client;
    private bool _running;
    private bool _allowClose;
    private bool _exitRequested;
    private bool _imeComposing;
    private TaskCompletionSource<AcpPermissionDecision>? _permissionCompletion;
    private AcpPermissionRequest? _permissionRequest;

    internal event Action? ExitRequested;

    public OverlayWindow()
    {
        InitializeComponent();
        ApplyStrings();
        Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            _ = RequestExitAsync();
        };
    }

    internal void ShowActivation(ActivationRequest request)
    {
        if (_running)
        {
            PositionNearPointer(request.X, request.Y);
            Show();
            Activate();
            return;
        }
        var contextChanged = !string.Equals(_activation.Folder, request.Folder, StringComparison.OrdinalIgnoreCase) ||
            !_activation.Files.SequenceEqual(request.Files, StringComparer.OrdinalIgnoreCase);
        _activation = request;
        FolderText.Text = request.Folder;
        FilesText.Text = request.Files.Count == 0
            ? Strings.Get("未选择文件", "No files selected")
            : string.Join("  ·  ", request.Files.Select(Path.GetFileName));
        if (!_running)
        {
            if (contextChanged) PromptBox.Clear();
            PromptBox.Visibility = Visibility.Visible;
            RunPanel.Visibility = Visibility.Collapsed;
            PermissionPanel.Visibility = Visibility.Collapsed;
            OpenGooseButton.Visibility = Visibility.Collapsed;
            ConfigureGooseButton.Visibility = Visibility.Collapsed;
            RunButton.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Collapsed;
        }
        PositionNearPointer(request.X, request.Y);
        Show();
        Activate();
        PromptBox.Focus();
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();

    private async Task RunAsync()
    {
        var prompt = PromptBox.Text.Trim();
        if (_running || string.IsNullOrWhiteSpace(prompt)) return;
        var installation = GooseInstallation.Locate();
        if (installation is null)
        {
            SetError(Strings.Get("找不到 Goose CLI。请安装 Goose Desktop，或设置 GOOSE_CLI_PATH。", "Goose CLI was not found. Install Goose Desktop or set GOOSE_CLI_PATH."));
            return;
        }

        if (_client is null)
        {
            _client = new AcpClient(installation);
            _client.UpdateReceived += update => Dispatcher.Invoke(() => ApplyUpdate(update));
            _client.DiagnosticReceived += message => Dispatcher.Invoke(() => SetDiagnostic(message));
            _client.PermissionRequested += RequestPermissionAsync;
        }
        SetRunning(true);
        OutputText.Clear();
        StatusText.Text = Strings.Get("正在连接 Goose…", "Connecting to Goose…");
        try
        {
            await _client.NewSessionAsync(_activation.Folder);
            StatusText.Text = Strings.Get("Goose 正在工作…", "Goose is working…");
            await _client.PromptAsync(prompt, _activation.Files);
            StatusText.Text = Strings.Get("完成", "Completed");
            SetRunning(false, completed: true);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Strings.Get("已取消", "Cancelled");
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
            StatusText.Text = $"{Strings.Get("工具", "Tool")}: {update.Text}{status}";
            if (!string.IsNullOrWhiteSpace(update.Text)) AppendOutput($"\n• {update.Text}{status}\n");
        }
        else if (update.Kind == AcpUpdateKind.Message && !string.IsNullOrEmpty(update.Text)) AppendOutput(update.Text);
    }

    private Task<AcpPermissionDecision> RequestPermissionAsync(AcpPermissionRequest request)
    {
        var completion = new TaskCompletionSource<AcpPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.Invoke(() =>
        {
            _permissionRequest = request;
            _permissionCompletion = completion;
            PermissionText.Text = string.IsNullOrWhiteSpace(request.Title)
                ? Strings.Get("Goose 请求运行工具", "Goose requests permission to run a tool")
                : request.Title;
            PermissionPanel.Visibility = Visibility.Visible;
            StatusText.Text = Strings.Get("等待权限确认", "Waiting for permission");
            Activate();
            AllowButton.Focus();
        });
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
            : options.FirstOrDefault(value => value.Kind.Contains("reject", StringComparison.OrdinalIgnoreCase) || value.Kind.Contains("deny", StringComparison.OrdinalIgnoreCase));
        _permissionCompletion.TrySetResult(option is null ? AcpPermissionDecision.Cancelled : new AcpPermissionDecision(option.Id));
        _permissionCompletion = null;
        _permissionRequest = null;
        PermissionPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = Strings.Get("Goose 正在工作…", "Goose is working…");
    }

    private async void CancelRun_Click(object sender, RoutedEventArgs e)
    {
        CompletePermission(allow: false);
        if (_client is not null) await _client.CancelAsync();
        StatusText.Text = Strings.Get("正在取消…", "Cancelling…");
    }

    private void SetRunning(bool running, bool completed = false)
    {
        _running = running;
        PromptBox.Visibility = running || completed ? Visibility.Collapsed : Visibility.Visible;
        RunPanel.Visibility = running || completed ? Visibility.Visible : Visibility.Collapsed;
        RunButton.Visibility = running || completed ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        OpenGooseButton.Visibility = completed ? Visibility.Visible : Visibility.Collapsed;
        ConfigureGooseButton.Visibility = Visibility.Collapsed;
        HintText.Visibility = running || completed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AppendOutput(string text)
    {
        OutputText.AppendText(text);
        OutputText.ScrollToEnd();
    }

    internal void SetDiagnostic(string message)
    {
        if (_running && !string.IsNullOrWhiteSpace(message)) StatusText.Text = message;
    }

    private void SetError(string message)
    {
        PromptBox.Visibility = Visibility.Collapsed;
        RunPanel.Visibility = Visibility.Visible;
        StatusText.Text = Strings.Get("错误", "Error");
        OutputText.Text = message;
        ConfigureGooseButton.Visibility = Visibility.Visible;
    }

    internal void OpenGoose()
    {
        if (_client?.SessionId is { Length: > 0 } sessionId)
        {
            try { System.Windows.Clipboard.SetText(sessionId); } catch { }
        }
        var uri = _client?.SessionId is { Length: > 0 } id
            ? $"goose://resume/{Uri.EscapeDataString(id)}"
            : "goose://new-session";
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch
        {
            var installation = GooseInstallation.Locate();
            if (installation?.DesktopPath is not null) Process.Start(new ProcessStartInfo(installation.DesktopPath) { UseShellExecute = true });
        }
    }

    private async void OpenGoose_Click(object sender, RoutedEventArgs e)
    {
        OpenGoose();
        await RequestExitAsync();
    }

    private async void ConfigureGoose_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("goose://new-session") { UseShellExecute = true }); }
        catch
        {
            var installation = GooseInstallation.Locate();
            if (installation?.DesktopPath is not null) Process.Start(new ProcessStartInfo(installation.DesktopPath) { UseShellExecute = true });
        }
        await RequestExitAsync();
    }

    private async void Close_Click(object sender, RoutedEventArgs e) => await RequestExitAsync();
    private void DragRegion_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }

    private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            await RequestExitAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && PromptBox.IsKeyboardFocusWithin && !_imeComposing &&
                 (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) == ModifierKeys.None)
        {
            e.Handled = true;
            await RunAsync();
        }
    }

    private void PromptBox_TextInputStart(object sender, TextCompositionEventArgs e) => _imeComposing = true;
    private void PromptBox_TextInputUpdate(object sender, TextCompositionEventArgs e) => _imeComposing = true;
    private void PromptBox_TextInput(object sender, TextCompositionEventArgs e) => _imeComposing = false;

    private void ApplyStrings()
    {
        TitleText.Text = Strings.Get("在此位置使用 Goose", "Run Goose here");
        RunButton.Content = Strings.Get("运行", "Run");
        CancelButton.Content = Strings.Get("取消", "Cancel");
        OpenGooseButton.Content = Strings.Get("在 Goose 中打开", "Open in Goose");
        ConfigureGooseButton.Content = Strings.Get("在 Goose 中配置", "Configure in Goose");
        AllowButton.Content = Strings.Get("允许", "Allow");
        DenyButton.Content = Strings.Get("拒绝", "Deny");
        HintText.Text = Strings.Get("Enter 运行 · Shift/Ctrl+Enter 换行 · Esc 关闭", "Enter to run · Shift/Ctrl+Enter for newline · Esc to close");
    }

    private void PositionNearPointer(int? x, int? y)
    {
        var point = new System.Drawing.Point(x ?? System.Windows.Forms.Cursor.Position.X, y ?? System.Windows.Forms.Cursor.Position.Y);
        var screen = System.Windows.Forms.Screen.FromPoint(point);
        var dpi = GetDpiForPoint(point);
        var scale = dpi / 96d;
        var work = screen.WorkingArea;
        var desiredLeft = point.X / scale + 18;
        var desiredTop = point.Y / scale + 18;
        Left = Math.Clamp(desiredLeft, work.Left / scale, Math.Max(work.Left / scale, work.Right / scale - Width));
        Top = Math.Clamp(desiredTop, work.Top / scale, Math.Max(work.Top / scale, work.Bottom / scale - Height));
    }

    private static uint GetDpiForPoint(System.Drawing.Point point)
    {
        var monitor = MonitorFromPoint(new NativePoint(point.X, point.Y), 2);
        return GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX : 96;
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
            try { await _client.CancelAsync(); } catch { }
        }
        ExitRequested?.Invoke();
    }

    internal void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
