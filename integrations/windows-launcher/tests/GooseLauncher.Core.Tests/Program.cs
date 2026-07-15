using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using GooseLauncher.Core;

var tests = new (string Name, Action Run)[]
{
    ("activation accepts sibling files", AcceptsSiblingFiles),
    ("activation rejects a different parent", RejectsDifferentParent),
    ("activation rejects too many files", RejectsTooManyFiles),
    ("task prompt text names selected files", PromptNamesSelectedFiles),
    ("Desktop activation frames preserve Unicode inputs", FramesDesktopActivation),
    ("Desktop capabilities frames omit absent run fields", FramesDesktopCapabilities),
    ("Desktop activation client completes capabilities and run handshake", ActivatesDesktopOverUserPipe),
    ("Desktop cold start contains no task data", BuildsPrivateDesktopLaunchArguments),
    ("Goose locator honors explicit CLI path", LocatesExplicitCli),
    ("Goose locator honors Companion path overrides", LocatesCompanionOverrides),
    ("Goose locator pairs a Desktop override with its bundled CLI", PairsDesktopOverrideWithBundledCli),
    ("Goose locator rejects a missing CLI override", RejectsMissingCliOverride),
    ("Goose terminal launch preserves exact prompt arguments", BuildsInteractiveRunArguments),
    ("Goose CLI session launch uses the selected directory", BuildsInteractiveSessionArguments)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception error) { failures.Add($"FAIL {test.Name}: {error.Message}"); }
}
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

static void AcceptsSiblingFiles()
{
    using var workspace = TestWorkspace.Create();
    var one = workspace.File("one.txt");
    var two = workspace.File("two.txt");
    var request = ActivationRequest.Create(workspace.Path, files: [one, two]);
    Equal(2, request.Files.Count);
    Equal(workspace.Path, request.Folder);
}

static void RejectsDifferentParent()
{
    using var workspace = TestWorkspace.Create();
    var child = Directory.CreateDirectory(System.IO.Path.Combine(workspace.Path, "child")).FullName;
    var file = System.IO.Path.Combine(child, "nested.txt");
    File.WriteAllText(file, "test");
    Throws<InvalidDataException>(() => ActivationRequest.Create(workspace.Path, files: [file]));
}

static void RejectsTooManyFiles()
{
    using var workspace = TestWorkspace.Create();
    var files = Enumerable.Range(0, ActivationRequest.MaxFiles + 1).Select(index => workspace.File($"{index}.txt")).ToArray();
    Throws<InvalidDataException>(() => ActivationRequest.Create(workspace.Path, files: files));
}

static void PromptNamesSelectedFiles()
{
    using var workspace = TestWorkspace.Create("附件目录");
    var one = workspace.File("ui.png");
    var two = workspace.File("witch_icon.png");
    var prompt = TaskPromptText.Build("这两个相同吗？", [one, two]);
    if (!prompt.StartsWith("这两个相同吗？", StringComparison.Ordinal)) throw new Exception("Original task text was changed.");
    if (!prompt.Contains(one, StringComparison.Ordinal) || !prompt.Contains(two, StringComparison.Ordinal))
        throw new Exception("Selected file paths are missing from the ACP text fallback.");
}

static void FramesDesktopActivation()
{
    using var workspace = TestWorkspace.Create("协议 工作区");
    var file = workspace.File("示例 file.txt");
    const string prompt = "检查这些文件，不要改写输入。";
    var frame = DesktopActivationProtocol.EncodeRequest(
        "request-1",
        "run",
        new string('a', 64),
        workspace.Path,
        prompt,
        [file]);
    Equal(frame.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(frame));
    using var document = JsonDocument.Parse(frame.AsMemory(4));
    var root = document.RootElement;
    Equal(DesktopActivationProtocol.Version, root.GetProperty("protocolVersion").GetInt32());
    Equal(prompt, root.GetProperty("prompt").GetString());
    Equal(file, root.GetProperty("files")[0].GetString());
}

static void FramesDesktopCapabilities()
{
    var frame = DesktopActivationProtocol.EncodeRequest(
        "request-1",
        "capabilities",
        new string('a', 64));
    using var document = JsonDocument.Parse(frame.AsMemory(4));
    var root = document.RootElement;
    if (root.TryGetProperty("cwd", out _)) throw new Exception("Capabilities request contains a null cwd.");
    if (root.TryGetProperty("prompt", out _)) throw new Exception("Capabilities request contains a null prompt.");
}

static void BuildsPrivateDesktopLaunchArguments()
{
    using var workspace = TestWorkspace.Create("Desktop workspace");
    var cli = workspace.File("goose.exe");
    var desktop = workspace.File("Goose.exe");
    const string privatePrompt = "private prompt that must not be in process arguments";
    var privateFile = workspace.File("private file.txt");
    var startInfo = new GooseInstallation(cli, desktop).CreateDesktopStartInfo(forLauncherActivation: true);
    Equal(desktop, startInfo.FileName);
    Equal(true, startInfo.UseShellExecute);
    Equal(1, startInfo.ArgumentList.Count);
    Equal("--launcher-activation", startInfo.ArgumentList[0]);
    if (startInfo.ArgumentList.Any(value => value.Contains(privatePrompt, StringComparison.Ordinal) || value.Contains(privateFile, StringComparison.Ordinal)))
        throw new Exception("Desktop process arguments contain task data.");
}

static void ActivatesDesktopOverUserPipe() => ActivatesDesktopOverUserPipeAsync().GetAwaiter().GetResult();

static async Task ActivatesDesktopOverUserPipeAsync()
{
    using var workspace = TestWorkspace.Create("activation client");
    var endpointPath = System.IO.Path.Combine(workspace.Path, "desktop-activation.json");
    var pipeName = $"GooseLauncher.Tests.{Guid.NewGuid():N}";
    var authToken = new string('b', 64);
    var cli = workspace.File("goose.exe");
    var desktop = workspace.File("Goose.exe");
    var selectedFile = workspace.File("示例.txt");
    const string prompt = "读取这个文件";
    var endpoint = new DesktopActivationEndpoint(DesktopActivationProtocol.Version, Environment.ProcessId, $@"\\.\pipe\{pipeName}", authToken);
    File.WriteAllText(endpointPath, JsonSerializer.Serialize(endpoint, DesktopActivationProtocol.JsonOptions));

    string? receivedPrompt = null;
    string? receivedFile = null;
    var serverTask = Task.Run(async () =>
    {
        for (var requestIndex = 0; requestIndex < 2; requestIndex++)
        {
            await using var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await server.WaitForConnectionAsync();
            var header = new byte[4];
            await server.ReadExactlyAsync(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            var payload = new byte[length];
            await server.ReadExactlyAsync(payload);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var requestId = root.GetProperty("requestId").GetString()!;
            var action = root.GetProperty("action").GetString();
            DesktopActivationResponse response;
            if (action == "capabilities")
            {
                response = new DesktopActivationResponse(
                    DesktopActivationProtocol.Version,
                    requestId,
                    "accepted",
                    Capabilities: new DesktopActivationCapabilities(
                        ["ping", "capabilities", "run", "open"],
                        DesktopActivationProtocol.MaxPayloadBytes,
                        64 * 1024,
                        ActivationRequest.MaxFiles));
            }
            else
            {
                receivedPrompt = root.GetProperty("prompt").GetString();
                receivedFile = root.GetProperty("files")[0].GetString();
                response = new DesktopActivationResponse(DesktopActivationProtocol.Version, requestId, "accepted");
            }

            var responsePayload = JsonSerializer.SerializeToUtf8Bytes(response, DesktopActivationProtocol.JsonOptions);
            var responseFrame = new byte[responsePayload.Length + 4];
            BinaryPrimitives.WriteInt32LittleEndian(responseFrame, responsePayload.Length);
            responsePayload.CopyTo(responseFrame.AsSpan(4));
            await server.WriteAsync(responseFrame);
            await server.FlushAsync();
        }
    });

    var client = new DesktopActivationClient(endpointPath, _ => throw new Exception("Desktop should already be reachable."));
    await client.RunAsync(new GooseInstallation(cli, desktop), workspace.Path, prompt, [selectedFile]);
    await serverTask;
    Equal(prompt, receivedPrompt);
    Equal(selectedFile, receivedFile);
}

static void LocatesExplicitCli()
{
    using var workspace = TestWorkspace.Create();
    var cli = workspace.File("goose.exe");
    var previous = Environment.GetEnvironmentVariable("GOOSE_CLI_PATH");
    try
    {
        Environment.SetEnvironmentVariable("GOOSE_CLI_PATH", cli);
        Equal(cli, GooseInstallation.Locate()?.CliPath);
    }
    finally { Environment.SetEnvironmentVariable("GOOSE_CLI_PATH", previous); }
}

static void LocatesCompanionOverrides()
{
    using var workspace = TestWorkspace.Create();
    var cli = workspace.File("goose.exe");
    var desktop = workspace.File("Goose.exe");
    var installation = GooseInstallation.Locate(cli, desktop) ?? throw new Exception("Configured Goose paths were not resolved.");
    Equal(cli, installation.CliPath);
    Equal(desktop, installation.DesktopPath);
}

static void RejectsMissingCliOverride()
{
    using var workspace = TestWorkspace.Create();
    var missing = System.IO.Path.Combine(workspace.Path, "missing-goose.exe");
    Equal<GooseInstallation?>(null, GooseInstallation.Locate(missing));
}

static void PairsDesktopOverrideWithBundledCli()
{
    using var workspace = TestWorkspace.Create();
    var desktopDirectory = Directory.CreateDirectory(System.IO.Path.Combine(workspace.Path, "desktop")).FullName;
    var desktop = System.IO.Path.Combine(desktopDirectory, "Goose.exe");
    File.WriteAllText(desktop, "test");
    var cliDirectory = Directory.CreateDirectory(System.IO.Path.Combine(desktopDirectory, "resources", "bin")).FullName;
    var cli = System.IO.Path.Combine(cliDirectory, "goose.exe");
    File.WriteAllText(cli, "test");
    var installation = GooseInstallation.Locate(desktopOverride: desktop) ?? throw new Exception("Desktop override was not resolved.");
    Equal(cli, installation.CliPath);
    Equal(desktop, installation.DesktopPath);
}

static void BuildsInteractiveRunArguments()
{
    using var workspace = TestWorkspace.Create("terminal workspace");
    var cli = workspace.File("goose.exe");
    const string prompt = "Inspect these exact inputs; keep the semicolon.\r\n1. C:\\example path\\ui.png";
    var startInfo = new GooseInstallation(cli, null).CreateInteractiveRunStartInfo(workspace.Path, prompt);
    Equal(cli, startInfo.FileName);
    Equal(Path.GetFullPath(workspace.Path), startInfo.WorkingDirectory);
    Equal(true, startInfo.UseShellExecute);
    Equal(4, startInfo.ArgumentList.Count);
    Equal("run", startInfo.ArgumentList[0]);
    Equal("--text", startInfo.ArgumentList[1]);
    Equal(prompt, startInfo.ArgumentList[2]);
    Equal("--interactive", startInfo.ArgumentList[3]);
}

static void BuildsInteractiveSessionArguments()
{
    using var workspace = TestWorkspace.Create("CLI workspace");
    var cli = workspace.File("goose.exe");
    var startInfo = new GooseInstallation(cli, null).CreateInteractiveSessionStartInfo(workspace.Path);
    Equal(cli, startInfo.FileName);
    Equal(Path.GetFullPath(workspace.Path), startInfo.WorkingDirectory);
    Equal(true, startInfo.UseShellExecute);
    Equal(1, startInfo.ArgumentList.Count);
    Equal("session", startInfo.ArgumentList[0]);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

sealed class TestWorkspace : IDisposable
{
    private TestWorkspace(string path) => Path = path;
    internal string Path { get; }
    internal static TestWorkspace Create(string? name = null)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GooseLauncher.Tests", Guid.NewGuid().ToString("N"), name ?? "workspace");
        Directory.CreateDirectory(path);
        return new TestWorkspace(path);
    }
    internal string File(string name)
    {
        var path = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllText(path, "test");
        return path;
    }
    public void Dispose()
    {
        var root = Directory.GetParent(Path)?.FullName;
        if (root is not null && root.StartsWith(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GooseLauncher.Tests"), StringComparison.OrdinalIgnoreCase))
            Directory.Delete(root, recursive: true);
    }
}
