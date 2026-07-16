using System.Security.Principal;
using GooseLauncher.Core;
using Microsoft.Windows.AppLifecycle;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace GooseLauncher.App;

public partial class App : Application
{
    internal static App? Instance { get; private set; }

    private Mutex? _singleInstance;
    private ActivationPipeServer? _pipeServer;
    private OverlayWindow? _overlay;
    private SettingsWindow? _settings;
    private SystemTrayIcon? _tray;
    private bool _exiting;

    public App()
    {
        Instance = this;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ = args;
        var launchToTray = IsTrayLaunch(Program.CommandLineArgs) ||
            AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask;
        var launchToSettings = IsSettingsLaunch(Program.CommandLineArgs);
        var request = ParseActivation(Program.CommandLineArgs);
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _singleInstance = new Mutex(true, $"Local\\GooseLauncher.{sid}", out var firstInstance);
        if (!firstInstance)
        {
            if (launchToTray)
                Exit();
            else if (launchToSettings)
                _ = RedirectSettingsAndExitAsync();
            else
                _ = RedirectAndExitAsync(request ?? DefaultActivation());
            return;
        }

        _overlay = new OverlayWindow();

        _pipeServer = new ActivationPipeServer(DispatchActivationAsync, DispatchSettingsAsync);
        _pipeServer.DiagnosticReceived += message =>
            _overlay.DispatcherQueue.TryEnqueue(() => _overlay.SetDiagnostic(message));
        _pipeServer.Start();

        _tray = new SystemTrayIcon();
        _tray.OpenLauncherRequested += ShowLauncher;
        _tray.OpenGooseRequested += OpenGooseFromTray;
        _tray.OpenCliRequested += OpenCliFromTray;
        _tray.SettingsRequested += () => ShowSettings(_overlay.AppWindow);
        _tray.ExitRequested += ExitApp;
        _tray.Initialize();
        ApplyQuickLauncherShortcut();
        LauncherDiagnostics.Record("launcher_start", "ready");

        if (launchToSettings)
            ShowSettings(_overlay.AppWindow);
        else if (!launchToTray)
            _overlay.ShowActivation(request ?? DefaultActivation());
    }

    private Task DispatchSettingsAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_overlay?.DispatcherQueue.TryEnqueue(() =>
        {
            ShowSettings(_overlay.AppWindow);
            completion.SetResult();
        }) != true)
            completion.SetException(new InvalidOperationException("Launcher UI is unavailable."));
        return completion.Task;
    }

    private Task<ActivationAcceptance> DispatchActivationAsync(ActivationRequest activation)
    {
        if (_overlay is null) return Task.FromResult(ActivationAcceptance.Rejected);

        var completion = new TaskCompletionSource<ActivationAcceptance>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_overlay.DispatcherQueue.TryEnqueue(() =>
        {
            try { completion.SetResult(_overlay.TryShowActivation(activation)); }
            catch (Exception error) { completion.SetException(error); }
        }))
        {
            completion.SetResult(ActivationAcceptance.Rejected);
        }
        return completion.Task;
    }

    private async Task RedirectAndExitAsync(ActivationRequest request)
    {
        await ActivationPipeServer.TrySendAsync(request, TimeSpan.FromSeconds(3));
        _singleInstance?.Dispose();
        Exit();
    }

    private async Task RedirectSettingsAndExitAsync()
    {
        await ActivationPipeServer.TrySendSettingsAsync(TimeSpan.FromSeconds(3));
        _singleInstance?.Dispose();
        Exit();
    }

    private static bool IsTrayLaunch(string[] args)
    {
        if (args.Any(value => value.Equals("--tray", StringComparison.OrdinalIgnoreCase))) return true;
        foreach (var value in args)
        {
            if (!value.StartsWith("goosecompanion:", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (new Uri(value).Host.Equals("tray", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
        }
        return false;
    }

    private static bool IsSettingsLaunch(string[] args)
    {
        if (args.Any(value => value.Equals("--settings", StringComparison.OrdinalIgnoreCase))) return true;
        foreach (var value in args)
        {
            if (!value.StartsWith("goosecompanion:", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (new Uri(value).Host.Equals("settings", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
        }
        return false;
    }

    private static ActivationRequest DefaultActivation() => ActivationRequest.Create(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static ActivationRequest? ParseActivation(string[] args)
    {
        try
        {
            var folderIndex = Array.FindIndex(args, value => value.Equals("--folder", StringComparison.OrdinalIgnoreCase));
            if (folderIndex >= 0 && folderIndex + 1 < args.Length)
                return ActivationRequest.Create(args[folderIndex + 1]);
        }
        catch
        {
            // Invalid external activations fall back to the current directory.
        }

        return null;
    }

    internal void ShowSettings(AppWindow? relativeTo = null)
    {
        _settings ??= CreateSettingsWindow();
        _settings.ShowSettings(relativeTo);
    }

    private SettingsWindow CreateSettingsWindow()
    {
        var window = new SettingsWindow();
        window.SettingsSaved += () =>
        {
            ApplyQuickLauncherShortcut();
            if (_overlay is not null) _ = _overlay.OnSettingsChangedAsync();
        };
        return window;
    }

    internal void SetBusy(bool busy) => _tray?.SetBusy(busy);

    internal void ShowNotification(string title, string message) => _tray?.ShowNotification(title, message);

    private void ShowLauncher()
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _overlay?.TryShowActivation(ActivationRequest.Create(folder));
    }

    private void ApplyQuickLauncherShortcut()
    {
        if (_tray?.SetQuickLauncherShortcut(CompanionSettingsStore.Load().QuickLauncherShortcut) == false)
        {
            LauncherDiagnostics.Record("global_shortcut", "registration_failed");
            ShowNotification(
                Strings.Get("Goose 快捷键不可用", "Goose shortcut unavailable"),
                Strings.Get("请在 Launcher 设置中选择其他快捷键。", "Choose another shortcut in Launcher settings."));
        }
        else
        {
            LauncherDiagnostics.Record("global_shortcut", "registered");
        }
    }

    private void OpenGooseFromTray()
    {
        try
        {
            var installation = GooseInstallation.Locate()
                ?? throw new FileNotFoundException(Strings.Get("Goose 安装不完整，请重新安装。", "The Goose installation is incomplete. Reinstall Goose."));
            GooseProcessLauncher.OpenDesktop(installation);
        }
        catch (Exception error)
        {
            ShowNotification(Strings.Get("无法打开 Goose", "Could not open Goose"), error.Message);
        }
    }

    private void OpenCliFromTray()
    {
        try
        {
            var installation = GooseInstallation.Locate()
                ?? throw new FileNotFoundException(Strings.Get("Goose 安装不完整，请重新安装。", "The Goose installation is incomplete. Reinstall Goose."));
            var folder = _overlay?.CurrentFolderForLaunch
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            GooseProcessLauncher.OpenTerminalSession(installation, folder);
        }
        catch (Exception error)
        {
            ShowNotification(Strings.Get("无法打开 Goose CLI", "Could not open Goose CLI"), error.Message);
        }
    }

    private async void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;

        if (_pipeServer is not null) await _pipeServer.DisposeAsync();
        if (_overlay is not null) await _overlay.DisposeSessionAsync();

        _tray?.Dispose();
        _settings?.CloseForExit();
        _overlay?.CloseForExit();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        Exit();
    }
}
