using System.Security.Principal;
using GooseLauncher.Core;
using Microsoft.UI.Xaml;

namespace GooseLauncher.App;

public partial class App : Application
{
    internal static App? Instance { get; private set; }

    private Mutex? _singleInstance;
    private ActivationPipeServer? _pipeServer;
    private OverlayWindow? _overlay;
    private bool _exiting;

    public App()
    {
        Instance = this;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var request = ParseActivation(Program.CommandLineArgs);
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _singleInstance = new Mutex(true, $"Local\\GooseLauncher.{sid}", out var firstInstance);
        if (!firstInstance)
        {
            if (request is not null)
                _ = RedirectAndExitAsync(request);
            else
                Exit();
            return;
        }

        _overlay = new OverlayWindow();
        _overlay.ExitRequested += ExitApp;

        _pipeServer = new ActivationPipeServer();
        _pipeServer.ActivationReceived += activation =>
            _overlay.DispatcherQueue.TryEnqueue(() => _overlay.ShowActivation(activation));
        _pipeServer.DiagnosticReceived += message =>
            _overlay.DispatcherQueue.TryEnqueue(() => _overlay.SetDiagnostic(message));
        _pipeServer.Start();

        _overlay.ShowActivation(request ?? ActivationRequest.Create(Environment.CurrentDirectory));
    }

    private async Task RedirectAndExitAsync(ActivationRequest request)
    {
        await ActivationPipeServer.TrySendAsync(request, TimeSpan.FromSeconds(3));
        Exit();
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
        catch
        {
            // Invalid external activations fall back to the current directory. The
            // overlay remains usable and does not expose a second diagnostics UI.
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
        Exit();
    }
}
