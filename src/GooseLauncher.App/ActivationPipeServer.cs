using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using GooseLauncher.Core;

namespace GooseLauncher.App;

internal sealed class ActivationPipeServer : IAsyncDisposable
{
    private const int MaxPayloadBytes = 32 * 1024;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listener;

    internal static string PipeName
    {
        get
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            return $"GooseLauncher.Activation.{sid.Replace('-', '_')}";
        }
    }

    internal event Action<ActivationRequest>? ActivationReceived;
    internal event Action<string>? DiagnosticReceived;

    internal void Start() => _listener ??= ListenAsync(_shutdown.Token);

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);
                var request = await ReadAsync(pipe, cancellationToken);
                ActivationReceived?.Invoke(request);
                await pipe.WriteAsync(new byte[] { 1 }, cancellationToken);
                await pipe.FlushAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception error) { DiagnosticReceived?.Invoke(error.Message); }
        }
    }

    private static async Task<ActivationRequest> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxPayloadBytes) throw new InvalidDataException("Invalid activation payload length.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.GetProperty("protocolVersion").GetInt32() != 1 || root.GetProperty("type").GetString() != "show_prompt")
            throw new InvalidDataException("Unsupported activation request.");
        var files = root.TryGetProperty("files", out var array)
            ? array.EnumerateArray().Select(item => item.GetString()).ToArray()
            : [];
        return ActivationRequest.Create(root.GetProperty("folder").GetString()!,
            root.TryGetProperty("x", out var x) ? x.GetInt32() : null,
            root.TryGetProperty("y", out var y) ? y.GetInt32() : null,
            files);
    }

    internal static async Task<bool> TrySendAsync(ActivationRequest request, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            while (true)
            {
                try { await pipe.ConnectAsync(250, cancellation.Token); break; }
                catch (TimeoutException) when (!cancellation.IsCancellationRequested) { }
            }
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { protocolVersion = 1, type = "show_prompt", folder = request.Folder, files = request.Files, x = request.X, y = request.Y });
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await pipe.WriteAsync(header, cancellation.Token);
            await pipe.WriteAsync(payload, cancellation.Token);
            await pipe.FlushAsync(cancellation.Token);
            var ack = new byte[1];
            await pipe.ReadExactlyAsync(ack, cancellation.Token);
            return ack[0] == 1;
        }
        catch { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        if (_listener is not null) try { await _listener; } catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }
}
