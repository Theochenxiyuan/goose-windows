using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace GooseLauncher.Core;

public sealed class DesktopActivationClient
{
    private const string PipePrefix = @"\\.\pipe\";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private readonly string _endpointPath;
    private readonly Action<GooseInstallation> _startDesktop;

    public DesktopActivationClient()
        : this(DefaultEndpointPath, installation =>
        {
            var startInfo = installation.CreateDesktopStartInfo(forLauncherActivation: true);
            Process.Start(startInfo);
        })
    {
    }

    internal DesktopActivationClient(string endpointPath, Action<GooseInstallation> startDesktop)
    {
        _endpointPath = endpointPath;
        _startDesktop = startDesktop;
    }

    public static string DefaultEndpointPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Goose",
        "launcher",
        "desktop-activation.json");

    public async Task RunAsync(
        GooseInstallation installation,
        string cwd,
        string prompt,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("D");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultTimeout);
        var started = false;
        try
        {
            while (true)
            {
                var endpoint = await TryReadEndpointAsync(timeout.Token);
                if (endpoint is not null)
                {
                    try
                    {
                        await EnsureCompatibleAsync(endpoint, timeout.Token);
                        var response = await SendAsync(endpoint, DesktopActivationProtocol.EncodeRequest(
                            requestId,
                            "run",
                            endpoint.AuthToken,
                            Path.GetFullPath(cwd),
                            prompt,
                            files.Select(Path.GetFullPath).ToArray()), timeout.Token);
                        ValidateAccepted(response, requestId);
                        return;
                    }
                    catch (DesktopActivationRejectedException) { throw; }
                    catch (NotSupportedException) { throw; }
                    catch (Exception error) when (error is IOException or TimeoutException or JsonException)
                    {
                    }
                }

                if (!started)
                {
                    _startDesktop(installation);
                    started = true;
                }
                await Task.Delay(150, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Goose Desktop did not accept the task before the activation timeout.");
        }
    }

    private async Task EnsureCompatibleAsync(
        DesktopActivationEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        if (endpoint.ProtocolVersion != DesktopActivationProtocol.Version)
            throw new NotSupportedException(
                $"Goose Desktop activation protocol {endpoint.ProtocolVersion} is not supported.");

        var requestId = Guid.NewGuid().ToString("D");
        var response = await SendAsync(endpoint, DesktopActivationProtocol.EncodeRequest(
            requestId,
            "capabilities",
            endpoint.AuthToken), cancellationToken);
        ValidateAccepted(response, requestId);
        if (response.Capabilities?.Actions.Contains("run", StringComparer.Ordinal) != true)
            throw new NotSupportedException("Goose Desktop does not support Launcher run activation.");
    }

    private async Task<DesktopActivationEndpoint?> TryReadEndpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                _endpointPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var endpoint = await JsonSerializer.DeserializeAsync<DesktopActivationEndpoint>(
                stream,
                DesktopActivationProtocol.JsonOptions,
                cancellationToken);
            if (endpoint is null)
                return null;

            try
            {
                using var process = Process.GetProcessById(endpoint.Pid);
                return process.HasExited ? null : endpoint;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or IOException or JsonException)
        {
            return null;
        }
    }

    private static async Task<DesktopActivationResponse> SendAsync(
        DesktopActivationEndpoint endpoint,
        byte[] frame,
        CancellationToken cancellationToken)
    {
        var pipeName = endpoint.PipeName.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase)
            ? endpoint.PipeName[PipePrefix.Length..]
            : endpoint.PipeName;
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new InvalidDataException("Goose Desktop published an invalid activation endpoint.");

        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(500, cancellationToken);
        await pipe.WriteAsync(frame, cancellationToken);
        await pipe.FlushAsync(cancellationToken);
        return await DesktopActivationProtocol.ReadResponseAsync(pipe, cancellationToken);
    }

    private static void ValidateAccepted(DesktopActivationResponse response, string requestId)
    {
        if (response.ProtocolVersion != DesktopActivationProtocol.Version)
            throw new NotSupportedException(
                $"Goose Desktop selected activation protocol {response.ProtocolVersion}.");
        if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
            throw new InvalidDataException("Goose Desktop returned a mismatched activation response.");
        if (!string.Equals(response.Status, "accepted", StringComparison.Ordinal))
            throw new DesktopActivationRejectedException(
                string.IsNullOrWhiteSpace(response.Message) ? "Goose Desktop rejected the task." : response.Message);
    }
}
