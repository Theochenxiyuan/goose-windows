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
    private const int DefaultHeight = 420;
    private const int MinimumWidth = 420;
    private const int MinimumHeight = 360;
    private const int MaximumWidth = 900;
    private const int MaximumHeight = 600;
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
    private CancellationTokenSource? _runCancellation;
    private bool _running;
    private bool _hasActivationContext;
    private bool _allowClose;
    private uint _currentDpi = 96;
    private uint? _movePointerId;
    private NativePoint _moveStartCursor;
    private Windows.Graphics.PointInt32 _moveStartPosition;
    private int _launcherOptionsLoadVersion;
    private LauncherOptionsCatalog? _launcherOptions;

    internal string CurrentFolderForLaunch => _hasActivationContext
        ? _activation.Folder
        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public OverlayWindow()
    {
        InitializeComponent();
        ApplyStrings();
        InitializeModelSelectors();

        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        GooseBranding.ApplyIcon(AppWindow);
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
        _ = LoadLauncherOptionsAsync();
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
        var installation = GooseInstallation.Locate();
        var sessionSelection = GetSessionSelection();
        if (installation is null)
        {
            SetError(Strings.Get(
                "Goose 安装不完整，请重新安装。",
                "The Goose installation is incomplete. Reinstall Goose."));
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
                    TaskPromptText.Build(prompt, _activation.Files),
                    sessionSelection);
                AppWindow.Hide();
                await CompleteRunAsync();
            }
            catch (Exception error)
            {
                SetError(error.Message);
            }
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _runCancellation = cancellation;
        try
        {
            await new DesktopActivationClient().RunAsync(
                installation,
                _activation.Folder,
                prompt,
                _activation.Files,
                sessionSelection,
                cancellation.Token);
            AppWindow.Hide();
            await CompleteRunAsync();
        }
        catch (OperationCanceledException)
        {
            SetSubmitting(false);
        }
        catch (Exception error)
        {
            SetError(error.Message);
        }
        finally
        {
            if (ReferenceEquals(_runCancellation, cancellation)) _runCancellation = null;
        }
    }

    private Task CompleteRunAsync()
    {
        _running = false;
        PromptTextBox.Text = string.Empty;
        SetSubmitting(false);
        return Task.CompletedTask;
    }

    private void SetSubmitting(bool submitting)
    {
        _running = submitting;
        PromptTextBox.IsEnabled = !submitting;
        ModelComboBox.IsEnabled = !submitting;
        ThinkingEffortComboBox.IsEnabled = !submitting;
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
        if (_running && !string.IsNullOrWhiteSpace(message)) HintTextBlock.Text = message;
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
            var installation = GooseInstallation.Locate()
                ?? throw new FileNotFoundException(Strings.Get("Goose 安装不完整，请重新安装。", "The Goose installation is incomplete. Reinstall Goose."));
            if (settings.RunTarget == GooseRunTarget.Desktop)
            {
                GooseProcessLauncher.OpenDesktop(installation);
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

    private sealed record ModelChoice(
        string Label,
        string? Provider,
        string? Model,
        bool Reasoning);

    private sealed record ThinkingEffortChoice(string Value, string Label);

    private void InitializeModelSelectors()
    {
        ModelComboBox.ItemsSource = new[] { DefaultModelChoice() };
        ModelComboBox.SelectedIndex = 0;
        ThinkingEffortComboBox.ItemsSource = ThinkingEffortChoices();
        ThinkingEffortComboBox.SelectedIndex = 0;
    }

    private ModelChoice DefaultModelChoice()
    {
        var suffix = _launcherOptions is { DefaultProvider: not null, DefaultModel: not null }
            ? $" ({_launcherOptions.DefaultProvider}/{_launcherOptions.DefaultModel})"
            : string.Empty;
        return new ModelChoice(
            Strings.Get("使用 Goose 默认设置", "Use Goose default") + suffix,
            null,
            null,
            false);
    }

    private static IReadOnlyList<ThinkingEffortChoice> ThinkingEffortChoices() =>
    [
        new("off", Strings.Get("关闭", "Off")),
        new("low", Strings.Get("低", "Low")),
        new("medium", Strings.Get("中", "Medium")),
        new("high", Strings.Get("高", "High")),
        new("max", Strings.Get("最高", "Max")),
    ];

    private async Task LoadLauncherOptionsAsync()
    {
        var installation = GooseInstallation.Locate();
        if (installation is null) return;

        var loadVersion = ++_launcherOptionsLoadVersion;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            var catalog = await new LauncherOptionsClient().GetAsync(installation, timeout.Token);
            if (loadVersion != _launcherOptionsLoadVersion) return;

            var previous = ModelComboBox.SelectedItem as ModelChoice;
            _launcherOptions = catalog;
            ToolTipService.SetToolTip(ModelComboBox, null);
            var choices = new List<ModelChoice> { DefaultModelChoice() };
            choices.AddRange(catalog.Providers.SelectMany(provider =>
                provider.Models.Select(model => new ModelChoice(
                    $"{model.Name}  ·  {provider.Name}",
                    provider.Id,
                    model.Id,
                    model.Reasoning))));
            ModelComboBox.ItemsSource = choices;
            ModelComboBox.SelectedItem = choices.FirstOrDefault(choice =>
                choice.Provider == previous?.Provider && choice.Model == previous?.Model) ?? choices[0];

            var defaultEffort = catalog.DefaultThinkingEffort ?? "off";
            ThinkingEffortComboBox.SelectedItem = ThinkingEffortChoices()
                .FirstOrDefault(choice => choice.Value == defaultEffort)
                ?? ThinkingEffortChoices()[0];
        }
        catch (Exception)
        {
            ToolTipService.SetToolTip(
                ModelComboBox,
                Strings.Get("暂时无法加载模型列表，将使用 Goose 默认设置。", "Models are temporarily unavailable; Goose defaults will be used."));
        }
    }

    private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ThinkingEffortComboBox.Visibility =
            ModelComboBox.SelectedItem is ModelChoice { Reasoning: true }
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private LauncherSessionSelection? GetSessionSelection()
    {
        if (ModelComboBox.SelectedItem is not ModelChoice { Provider: not null, Model: not null } model)
            return null;
        var thinkingEffort = model.Reasoning &&
            ThinkingEffortComboBox.SelectedItem is ThinkingEffortChoice effort
                ? effort.Value
                : null;
        return new LauncherSessionSelection(model.Provider, model.Model, thinkingEffort);
    }

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
        ModelComboBox.Header = Strings.Get("模型", "Model");
        ThinkingEffortComboBox.Header = Strings.Get("推理强度", "Reasoning effort");
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

    public ValueTask DisposeSessionAsync()
    {
        _runCancellation?.Cancel();
        return ValueTask.CompletedTask;
    }

    internal Task OnSettingsChangedAsync()
    {
        UpdateOpenTargetButton();
        return Task.CompletedTask;
    }

    private Task HideOverlayAsync()
    {
        if (_running)
        {
            _runCancellation?.Cancel();
            _running = false;
            SetSubmitting(false);
        }
        AppWindow.Hide();
        return Task.CompletedTask;
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
