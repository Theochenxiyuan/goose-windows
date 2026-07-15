using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GooseLauncher.Core;

public sealed class AcpClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GooseInstallation _installation;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private Task? _reader;
    private Task? _stderr;
    private long _nextId;

    public AcpClient(GooseInstallation installation) => _installation = installation;

    public string? SessionId { get; private set; }
    public event Action<AcpUpdate>? UpdateReceived;
    public event Func<AcpPermissionRequest, Task<AcpPermissionDecision>>? PermissionRequested;
    public event Action<string>? DiagnosticReceived;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is not null) return;
        _process = Process.Start(_installation.CreateAcpStartInfo()) ?? throw new InvalidOperationException("Could not start goose acp.");
        _reader = ReadLoopAsync(_process.StandardOutput, _lifetime.Token);
        _stderr = ReadStderrAsync(_process.StandardError, _lifetime.Token);
        var result = await RequestAsync("initialize", new
        {
            protocolVersion = 1,
            clientCapabilities = new { },
            clientInfo = new { name = "goose-launcher", title = "Goose Launcher", version = "0.1.0" }
        }, cancellationToken);
        var version = result.TryGetProperty("protocolVersion", out var protocol) ? protocol.ToString() : "unknown";
        if (version is not ("1" or "v1")) throw new NotSupportedException($"Goose selected unsupported ACP version {version}.");
    }

    public async Task<string> NewSessionAsync(string cwd, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken);
        var result = await RequestAsync("session/new", new { cwd = Path.GetFullPath(cwd), mcpServers = Array.Empty<object>(), _meta = new { client = "goose-launcher" } }, cancellationToken);
        SessionId = result.GetProperty("sessionId").GetString() ?? throw new InvalidDataException("Goose returned an empty ACP session id.");
        return SessionId;
    }

    public async Task PromptAsync(string text, IReadOnlyList<string> files, CancellationToken cancellationToken = default)
    {
        var completion = await StartPromptAsync(text, files, cancellationToken);
        await completion;
    }

    public async Task<Task> StartPromptAsync(string text, IReadOnlyList<string> files, CancellationToken cancellationToken = default)
    {
        if (SessionId is null) throw new InvalidOperationException("Create a session before prompting.");
        var prompt = new List<object> { new { type = "text", text = BuildPromptText(text, files) } };
        foreach (var file in files)
        {
            var uri = new Uri(file).AbsoluteUri;
            var mimeType = GetImageMimeType(file);
            var fileLength = new FileInfo(file).Length;
            prompt.Add(new
            {
                type = "resource_link",
                uri,
                name = Path.GetFileName(file),
                mimeType,
                size = fileLength
            });
        }
        var response = await StartRequestAsync("session/prompt", new { sessionId = SessionId, prompt }, cancellationToken);
        return AwaitPromptAsync(response);
    }

    private static async Task AwaitPromptAsync(Task<JsonElement> response) => await response;

    public static string BuildPromptText(string text, IReadOnlyList<string> files)
    {
        if (files.Count == 0) return text;

        var result = new StringBuilder(text.TrimEnd());
        result.AppendLine();
        result.AppendLine();
        result.AppendLine("User-selected files (exact paths; treat these as explicit inputs to the task):");
        for (var index = 0; index < files.Count; index++)
            result.Append(index + 1).Append(". ").AppendLine(Path.GetFullPath(files[index]));
        return result.ToString().TrimEnd();
    }

    private static string? GetImageMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => null
    };

    public Task CancelAsync(CancellationToken cancellationToken = default)
    {
        if (SessionId is null) return Task.CompletedTask;
        return NotifyAsync("session/cancel", new { sessionId = SessionId }, cancellationToken);
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var response = await StartRequestAsync(method, parameters, cancellationToken);
        return await response.ConfigureAwait(false);
    }

    private async Task<Task<JsonElement>> StartRequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate ACP request id.");
        try
        {
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken);
            return AwaitResponseAsync(id, completion, cancellationToken);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    private async Task<JsonElement> AwaitResponseAsync(
        long id,
        TaskCompletionSource<JsonElement> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return await completion.Task.ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken) =>
        WriteAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        if (_process is null || _process.HasExited) throw new InvalidOperationException("goose acp is not running.");
        var json = JsonSerializer.Serialize(message, JsonOptions);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt64(out var id))
                {
                    if (root.TryGetProperty("method", out var requestMethod))
                        _ = HandleAgentRequestAsync(id, requestMethod.GetString() ?? string.Empty, root.TryGetProperty("params", out var requestParams) ? requestParams.Clone() : default);
                    else if (_pending.TryGetValue(id, out var completion))
                    {
                        if (root.TryGetProperty("error", out var error)) completion.TrySetException(new InvalidOperationException(FormatError(error)));
                        else completion.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
                    }
                    continue;
                }
                if (root.TryGetProperty("method", out var method))
                    HandleNotification(method.GetString() ?? string.Empty, root.TryGetProperty("params", out var parameters) ? parameters : default);
            }
            if (!_lifetime.IsCancellationRequested) FailPending(new EndOfStreamException("goose acp closed its output stream."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { DiagnosticReceived?.Invoke(error.Message); FailPending(error); }
    }

    private void HandleNotification(string method, JsonElement parameters)
    {
        if (method is not ("session/update" or "session/notification")) return;
        if (!parameters.TryGetProperty("update", out var update)) return;
        var name = update.TryGetProperty("sessionUpdate", out var kind) ? kind.GetString() ?? string.Empty : string.Empty;
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var content = update.TryGetProperty("content", out var block) ? block : default;
        var text = ExtractText(content);
        var toolId = GetString(update, "toolCallId") ?? GetString(update, "tool_call_id");
        var status = GetString(update, "status");
        var updateKind = normalized switch
        {
            "agentmessagechunk" => AcpUpdateKind.Message,
            "agentthoughtchunk" => AcpUpdateKind.Thought,
            "toolcall" or "toolcallupdate" => AcpUpdateKind.Tool,
            "plan" => AcpUpdateKind.Plan,
            _ => AcpUpdateKind.Other
        };
        if (updateKind == AcpUpdateKind.Tool && string.IsNullOrWhiteSpace(text))
            text = GetString(update, "title") ?? GetString(update, "name") ?? "Tool";
        UpdateReceived?.Invoke(new AcpUpdate(updateKind, text, toolId, status));
    }

    private async Task HandleAgentRequestAsync(long id, string method, JsonElement parameters)
    {
        try
        {
            if (method is not ("session/request_permission" or "requestPermission" or "session/requestPermission"))
            {
                await WriteAsync(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Method not supported" } }, _lifetime.Token);
                return;
            }
            var options = new List<AcpPermissionOption>();
            if (parameters.TryGetProperty("options", out var optionArray))
            {
                foreach (var option in optionArray.EnumerateArray())
                    options.Add(new AcpPermissionOption(GetString(option, "optionId") ?? GetString(option, "option_id") ?? string.Empty, GetString(option, "name") ?? GetString(option, "label") ?? "Allow", GetString(option, "kind") ?? string.Empty));
            }
            var toolCall = parameters.TryGetProperty("toolCall", out var tool) ? tool : default;
            var request = new AcpPermissionRequest(
                GetString(parameters, "toolCallId") ?? GetString(parameters, "tool_call_id") ?? GetString(toolCall, "toolCallId") ?? GetString(toolCall, "tool_call_id") ?? string.Empty,
                GetString(parameters, "title") ?? GetString(toolCall, "title") ?? "Goose requests permission",
                options);
            var handler = PermissionRequested;
            var decision = handler is null ? AcpPermissionDecision.Cancelled : await handler.Invoke(request);
            object outcome = decision.OptionId is null
                ? new { outcome = "cancelled" }
                : new { outcome = "selected", optionId = decision.OptionId };
            await WriteAsync(new { jsonrpc = "2.0", id, result = new { outcome } }, _lifetime.Token);
        }
        catch (Exception error)
        {
            DiagnosticReceived?.Invoke(error.Message);
            try { await WriteAsync(new { jsonrpc = "2.0", id, error = new { code = -32603, message = error.Message } }, _lifetime.Token); } catch { }
        }
    }

    private async Task ReadStderrAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line)) DiagnosticReceived?.Invoke(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static string ExtractText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Object) return string.Empty;
        return GetString(content, "text") ?? GetString(content, "title") ?? string.Empty;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string FormatError(JsonElement error) => GetString(error, "message") ?? error.ToString();

    private void FailPending(Exception error)
    {
        foreach (var completion in _pending.Values) completion.TrySetException(error);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        if (_process is not null)
        {
            try { _process.StandardInput.Close(); } catch { }
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); } catch { }
            }
            try { await _process.WaitForExitAsync(); } catch { }
            _process.Dispose();
        }
        if (_reader is not null) try { await _reader; } catch { }
        if (_stderr is not null) try { await _stderr; } catch { }
        _lifetime.Dispose();
        _writeLock.Dispose();
    }
}
