using GooseLauncher.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace GooseLauncher.App;

public sealed partial class SettingsWindow : Window
{
    private bool _allowClose;

    internal event Action? SettingsSaved;

    public SettingsWindow()
    {
        InitializeComponent();
        GooseBranding.ApplyIcon(AppWindow);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(620, 430));
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

    internal async void ShowSettings(AppWindow? relativeTo = null)
    {
        var settings = CompanionSettingsStore.Load();
        RunTargetComboBox.SelectedItem = settings.RunTarget == GooseRunTarget.Terminal ? TerminalTargetItem : DesktopTargetItem;
        QuickLauncherShortcutTextBox.Text = settings.QuickLauncherShortcut;
        ResultInfoBar.IsOpen = false;
        try
        {
            StartWithWindowsToggle.IsOn = await StartupRegistration.IsEnabledAsync();
            StartWithWindowsToggle.IsEnabled = true;
        }
        catch (Exception error)
        {
            StartWithWindowsToggle.IsEnabled = false;
            LauncherDiagnostics.Record("startup_task", error.GetType().Name);
        }
        if (relativeTo is not null) PositionRelativeTo(relativeTo);
        AppWindow.Show();
        Activate();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = (RunTargetComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "Terminal"
                ? GooseRunTarget.Terminal
                : GooseRunTarget.Desktop;
            var shortcut = QuickLauncherShortcutTextBox.Text.Trim();
            if (!SystemTrayIcon.IsSupportedShortcut(shortcut))
                throw new InvalidDataException(Strings.Get(
                    "快捷键格式无效。请使用 Ctrl/Alt/Shift/Win 加一个字母、数字或功能键。",
                    "Invalid shortcut. Use Ctrl/Alt/Shift/Win plus a letter, number, or function key."));
            CompanionSettingsStore.Save(new CompanionSettings(target, shortcut));
            if (StartWithWindowsToggle.IsEnabled)
                await StartupRegistration.SetEnabledAsync(StartWithWindowsToggle.IsOn);
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

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var data = new DataPackage();
        data.SetText(LauncherDiagnostics.CreateReport());
        Clipboard.SetContent(data);
        Clipboard.Flush();
        ResultInfoBar.Severity = InfoBarSeverity.Success;
        ResultInfoBar.Title = Strings.Get("诊断信息已复制", "Diagnostics copied");
        ResultInfoBar.Message = Strings.Get(
            "内容不包含提示词或文件路径。",
            "The report contains no prompts or file paths.");
        ResultInfoBar.IsOpen = true;
    }

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
        BoundaryInfoBar.Title = Strings.Get("统一安装", "Unified installation");
        BoundaryInfoBar.Message = Strings.Get(
            "Goose Desktop、CLI 和 Launcher 由同一个安装包管理。",
            "Goose Desktop, CLI, and Launcher are managed by one installation.");
        RunTargetComboBox.Header = Strings.Get("运行任务时打开", "Open when running tasks");
        DesktopTargetItem.Content = "Goose Desktop";
        TerminalTargetItem.Content = Strings.Get("终端", "Terminal");
        StartWithWindowsToggle.Header = Strings.Get("随 Windows 启动", "Start with Windows");
        StartWithWindowsToggle.OnContent = Strings.Get("开", "On");
        StartWithWindowsToggle.OffContent = Strings.Get("关", "Off");
        QuickLauncherShortcutTextBox.Header = Strings.Get("快速启动器快捷键", "Quick Launcher shortcut");
        CloseButton.Content = Strings.Get("关闭", "Close");
        CopyDiagnosticsButton.Content = Strings.Get("复制诊断信息", "Copy diagnostics");
        SaveButton.Content = Strings.Get("保存", "Save");
    }
}
