using System.Security.Principal;
using System.Windows;
using GooseLauncher.Core;

namespace GooseLauncher.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private ActivationPipeServer? _pipeServer;
    private OverlayWindow? _overlay;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // WPF's font cache still reads the legacy %windir% variable. Some launchers
        // only preserve %SystemRoot%, so restore the standard alias before a Window
        // type is initialized.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
            Environment.SetEnvironmentVariable("windir", Environment.GetEnvironmentVariable("SystemRoot"), EnvironmentVariableTarget.Process);
        var request = ParseActivation(e.Args);
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _singleInstance = new Mutex(true, $"Local\\GooseLauncher.{sid}", out var firstInstance);
        if (!firstInstance)
        {
            if (request is not null) await ActivationPipeServer.TrySendAsync(request, TimeSpan.FromSeconds(3));
            Shutdown();
            return;
        }

        _overlay = new OverlayWindow();
        _overlay.ExitRequested += ExitApp;
        _pipeServer = new ActivationPipeServer();
        _pipeServer.ActivationReceived += activation => Dispatcher.Invoke(() => _overlay.ShowActivation(activation));
        _pipeServer.DiagnosticReceived += message => _overlay.SetDiagnostic(message);
        _pipeServer.Start();

        _overlay.ShowActivation(request ?? ActivationRequest.Create(Environment.CurrentDirectory));
    }

    private static ActivationRequest? ParseActivation(string[] args)
    {
        try
        {
            var uriText = args.FirstOrDefault(value => value.StartsWith("goosecompanion:", StringComparison.OrdinalIgnoreCase));
            if (uriText is not null) return ActivationRequest.FromProtocolUri(new Uri(uriText));
            var folderIndex = Array.FindIndex(args, value => value.Equals("--folder", StringComparison.OrdinalIgnoreCase));
            if (folderIndex >= 0 && folderIndex + 1 < args.Length)
            {
                var fileIndex = Array.FindIndex(args, value => value.Equals("--files", StringComparison.OrdinalIgnoreCase));
                var files = fileIndex >= 0 ? args.Skip(fileIndex + 1).ToArray() : [];
                return ActivationRequest.Create(args[folderIndex + 1], files: files);
            }
        }
        catch (Exception error)
        {
            System.Windows.MessageBox.Show(error.Message, "Goose Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return null;
    }

    private async void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        if (_pipeServer is not null) await _pipeServer.DisposeAsync();
        if (_overlay is not null) await _overlay.DisposeSessionAsync();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        _overlay?.CloseForExit();
        Shutdown();
    }
}
