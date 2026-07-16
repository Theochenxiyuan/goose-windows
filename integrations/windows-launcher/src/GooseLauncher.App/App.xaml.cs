using System.Security.Principal;
using GooseLauncher.Core;
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
        var launchToTray = IsTrayLaunch(Program.CommandLineArgs);
        var launchToSettings = IsSettingsLaunch(Program.CommandLineArgs);
        var request = ParseActivation(Program.CommandLineArgs);
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _singleInstance = new Mutex(true, $"Local\\GooseLauncher.{sid}", out var firstInstance);
        if (!firstInstance)
        {
            if (launchToTray)
                Exit();
            else if (launchToSettings)
                Exit();
            else
                _ = RedirectAndExitAsync(request ?? ActivationRequest.Create(Environment.CurrentDirectory));
            return;
        }

        _overlay = new OverlayWindow();

        _pipeServer = new ActivationPipeServer(DispatchActivationAsync);
        _pipeServer.DiagnosticReceived += message =>
            _overlay.DispatcherQueue.TryEnqueue(() => _overlay.SetDiagnostic(message));
        _pipeServer.Start();

        _tray = new SystemTrayIcon();
        _tray.OpenGooseRequested += OpenGooseFromTray;
        _tray.OpenCliRequested += OpenCliFromTray;
        _tray.SettingsRequested += () => ShowSettings(_overlay.AppWindow);
        _tray.ExitRequested += ExitApp;
        _tray.Initialize();

        if (launchToSettings)
            ShowSettings(_overlay.AppWindow);
        else if (!launchToTray)
            _overlay.ShowActivation(request ?? ActivationRequest.Create(Environment.CurrentDirectory));
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

    private static bool IsSettingsLaunch(string[] args) =>
        args.Any(value => value.Equals("--settings", StringComparison.OrdinalIgnoreCase));

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
            if (_overlay is not null) _ = _overlay.OnSettingsChangedAsync();
        };
        return window;
    }

    internal void SetBusy(bool busy) => _tray?.SetBusy(busy);

    internal void ShowNotification(string title, string message) => _tray?.ShowNotification(title, message);

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
