using System.Text.Json;
using System.Text;
using GooseLauncher.Core;

var tests = new (string Name, Action Run)[]
{
    ("activation accepts sibling files", AcceptsSiblingFiles),
    ("activation rejects a different parent", RejectsDifferentParent),
    ("activation URI round-trips Unicode and coordinates", ParsesProtocolUri),
    ("activation rejects too many files", RejectsTooManyFiles),
    ("Goose locator honors explicit CLI path", LocatesExplicitCli)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception error) { failures.Add($"FAIL {test.Name}: {error.Message}"); }
}
foreach (var failure in failures) Console.Error.WriteLine(failure);
if (failures.Count == 0 && args.Contains("--integration", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        var installation = GooseInstallation.Locate() ?? throw new Exception("Goose CLI is not installed.");
        await using var client = new AcpClient(installation);
        var response = new StringBuilder();
        client.UpdateReceived += update => { if (update.Kind == AcpUpdateKind.Message) response.Append(update.Text); };
        client.DiagnosticReceived += message => Console.WriteLine($"ACP diagnostic: {message}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await client.StartAsync(timeout.Token);
        Console.WriteLine("PASS Goose ACP v1 initialize");
        var sessionId = await client.NewSessionAsync(Environment.CurrentDirectory, timeout.Token);
        Console.WriteLine($"PASS Goose ACP v1 initialize + session/new ({sessionId})");
        if (args.Contains("--prompt", StringComparer.OrdinalIgnoreCase))
        {
            await client.PromptAsync("Reply with exactly: Goose Launcher ACP OK", [], timeout.Token);
            if (response.Length == 0) throw new Exception("Goose completed session/prompt without an agent_message_chunk.");
            Console.WriteLine($"PASS Goose ACP session/prompt + agent_message_chunk ({response.ToString().Trim()})");
        }
    }
    catch (Exception error)
    {
        failures.Add($"FAIL Goose ACP integration: {error.Message}");
        Console.Error.WriteLine(failures[^1]);
    }
}
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

static void ParsesProtocolUri()
{
    using var workspace = TestWorkspace.Create("测试 工作区");
    var file = workspace.File("示例 file.txt");
    var files = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { file }));
    var folder = Uri.EscapeDataString(workspace.Path);
    var request = ActivationRequest.FromProtocolUri(new Uri($"goosecompanion://show?folder={folder}&files={files}&x=-120&y=1440"));
    Equal(-120, request.X);
    Equal(1440, request.Y);
    Equal(file, request.Files.Single());
}

static void RejectsTooManyFiles()
{
    using var workspace = TestWorkspace.Create();
    var files = Enumerable.Range(0, ActivationRequest.MaxFiles + 1).Select(index => workspace.File($"{index}.txt")).ToArray();
    Throws<InvalidDataException>(() => ActivationRequest.Create(workspace.Path, files: files));
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
