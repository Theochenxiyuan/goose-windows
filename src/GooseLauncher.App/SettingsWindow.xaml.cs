using GooseLauncher.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace GooseLauncher.App;

public sealed partial class SettingsWindow : Window
{
    private GooseInstallation? _detectedInstallation;
    private bool _allowClose;

    internal event Action? SettingsSaved;

    public SettingsWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(680, 580));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        AppWindow.Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            AppWindow.Hide();
        };
        ApplyStrings();
    }

    internal void ShowSettings(AppWindow? relativeTo = null)
    {
        var settings = CompanionSettingsStore.Load();
        _detectedInstallation = GooseInstallation.Locate();
        CliPathTextBox.Text = settings.CliPath ?? string.Empty;
        DesktopPathTextBox.Text = settings.DesktopPath ?? string.Empty;
        CliPathTextBox.PlaceholderText = _detectedInstallation?.CliPath ?? Strings.Get("未检测到 Goose CLI", "Goose CLI was not detected");
        DesktopPathTextBox.PlaceholderText = _detectedInstallation?.DesktopPath ?? Strings.Get("未检测到 Goose Desktop", "Goose Desktop was not detected");
        RunTargetComboBox.SelectedItem = settings.RunTarget == GooseRunTarget.Terminal ? TerminalTargetItem : DesktopTargetItem;
        StartWithWindowsToggle.IsOn = StartupRegistration.IsEnabled;
        ResultInfoBar.IsOpen = false;
        if (relativeTo is not null) PositionRelativeTo(relativeTo);
        AppWindow.Show();
        Activate();
    }

    private async void BrowseCli_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickExecutableAsync();
        if (path is not null) CliPathTextBox.Text = path;
    }

    private async void BrowseDesktop_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickExecutableAsync();
        if (path is not null) DesktopPathTextBox.Text = path;
    }

    private void AutoCli_Click(object sender, RoutedEventArgs e) => CliPathTextBox.Text = string.Empty;

    private void AutoDesktop_Click(object sender, RoutedEventArgs e) => DesktopPathTextBox.Text = string.Empty;

    private async Task<string?> PickExecutableAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cliPath = Normalize(CliPathTextBox.Text);
            var desktopPath = Normalize(DesktopPathTextBox.Text);
            if (cliPath is not null && !File.Exists(cliPath))
                throw new InvalidDataException(Strings.Get("Goose CLI 路径不存在。", "The Goose CLI path does not exist."));
            if (desktopPath is not null && !File.Exists(desktopPath))
                throw new InvalidDataException(Strings.Get("Goose Desktop 路径不存在。", "The Goose Desktop path does not exist."));

            var target = (RunTargetComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "Terminal"
                ? GooseRunTarget.Terminal
                : GooseRunTarget.Desktop;
            var effective = GooseInstallation.Locate(cliPath, desktopPath)
                ?? throw new InvalidDataException(Strings.Get("无法找到 Goose CLI。", "Goose CLI could not be found."));
            if (target == GooseRunTarget.Desktop && effective.DesktopPath is null)
                throw new InvalidDataException(Strings.Get("无法找到 Goose Desktop。", "Goose Desktop could not be found."));

            CompanionSettingsStore.Save(new CompanionSettings(cliPath, desktopPath, target));
            StartupRegistration.SetEnabled(StartWithWindowsToggle.IsOn);
            SettingsSaved?.Invoke();
            ResultInfoBar.Severity = InfoBarSeverity.Success;
            ResultInfoBar.Title = Strings.Get("已保存", "Saved");
            ResultInfoBar.Message = Strings.Get("新设置将用于下一个任务。", "The new settings will be used for the next task.");
            ResultInfoBar.IsOpen = true;
        }
        catch (Exception error)
        {
            ResultInfoBar.Severity = InfoBarSeverity.Error;
            ResultInfoBar.Title = Strings.Get("无法保存设置", "Could not save settings");
            ResultInfoBar.Message = error.Message;
            ResultInfoBar.IsOpen = true;
        }
    }

    private static string? Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
    }

    private void PositionRelativeTo(AppWindow owner)
    {
        var center = new Windows.Graphics.PointInt32(
            owner.Position.X + owner.Size.Width / 2,
            owner.Position.Y + owner.Size.Height / 2);
        var workArea = DisplayArea.GetFromPoint(center, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(
            Math.Clamp(center.X - AppWindow.Size.Width / 2, workArea.X, Math.Max(workArea.X, workArea.X + workArea.Width - AppWindow.Size.Width)),
            Math.Clamp(center.Y - AppWindow.Size.Height / 2, workArea.Y, Math.Max(workArea.Y, workArea.Y + workArea.Height - AppWindow.Size.Height))));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => AppWindow.Hide();

    internal void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void ApplyStrings()
    {
        Title = Strings.Get("Goose Launcher 设置", "Goose Launcher Settings");
        TitleTextBlock.Text = Title;
        SubtitleTextBlock.Text = Strings.Get(
            "这里只配置 Windows 集成。模型、凭据和扩展仍由 Goose 管理。",
            "Only Windows integration is configured here. Models, credentials, and extensions remain in Goose.");
        BoundaryInfoBar.Title = Strings.Get("设置边界", "Settings boundary");
        BoundaryInfoBar.Message = Strings.Get("留空路径将自动检测 Goose。", "Leave a path empty to auto-detect Goose.");
        CliPathTextBox.Header = Strings.Get("Goose CLI 路径", "Goose CLI path");
        DesktopPathTextBox.Header = Strings.Get("Goose Desktop 路径", "Goose Desktop path");
        BrowseCliButton.Content = BrowseDesktopButton.Content = Strings.Get("浏览…", "Browse…");
        AutoCliButton.Content = AutoDesktopButton.Content = Strings.Get("自动检测", "Auto");
        RunTargetComboBox.Header = Strings.Get("运行时打开", "Open tasks in");
        DesktopTargetItem.Content = "Goose Desktop";
        TerminalTargetItem.Content = Strings.Get("终端", "Terminal");
        StartWithWindowsToggle.Header = Strings.Get("随 Windows 启动", "Start with Windows");
        StartWithWindowsToggle.OnContent = Strings.Get("开", "On");
        StartWithWindowsToggle.OffContent = Strings.Get("关", "Off");
        CloseButton.Content = Strings.Get("关闭", "Close");
        SaveButton.Content = Strings.Get("保存", "Save");
    }
}
