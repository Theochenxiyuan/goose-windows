using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GooseLauncher.Core;

public sealed record DesktopActivationEndpoint(
    int ProtocolVersion,
    int Pid,
    string PipeName,
    string AuthToken);

public sealed record DesktopActivationCapabilities(
    IReadOnlyList<string> Actions,
    int MaxPayloadBytes,
    int MaxPromptLength,
    int MaxFiles,
    bool SessionSelection);

public sealed record DesktopActivationResponse(
    int ProtocolVersion,
    string RequestId,
    string Status,
    string? Code = null,
    string? Message = null,
    DesktopActivationCapabilities? Capabilities = null);

internal static class DesktopActivationProtocol
{
    internal const int Version = 2;
    internal const int MaxPayloadBytes = 256 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static byte[] EncodeRequest(
        string requestId,
        string action,
        string authToken,
        string? cwd = null,
        string? prompt = null,
        IReadOnlyList<string>? files = null,
        bool bringToFront = true,
        LauncherSessionSelection? sessionSelection = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocolVersion = Version,
            requestId,
            action,
            authToken,
            cwd,
            prompt,
            files = files ?? [],
            bringToFront,
            sessionSelection,
        }, JsonOptions);
        if (payload.Length is <= 0 or > MaxPayloadBytes)
            throw new InvalidDataException("Desktop activation payload length is invalid.");

        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    internal static async Task<DesktopActivationResponse> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxPayloadBytes)
            throw new InvalidDataException("Desktop activation response length is invalid.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<DesktopActivationResponse>(payload, JsonOptions)
            ?? throw new InvalidDataException("Desktop activation response is empty.");
    }
}

public sealed class DesktopActivationRejectedException(string message) : InvalidOperationException(message);
