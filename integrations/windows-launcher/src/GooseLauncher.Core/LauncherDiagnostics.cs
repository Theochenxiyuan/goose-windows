using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GooseLauncher.Core;

public static partial class LauncherDiagnostics
{
    private const long MaximumLogBytes = 256 * 1024;
    private static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GooseLauncher",
        "diagnostics.log");

    public static void Record(string stage, string code, long? durationMilliseconds = null)
    {
        var safeStage = Sanitize(stage);
        var safeCode = Sanitize(code);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaximumLogBytes)
                    File.Delete(LogPath);
                var line = JsonSerializer.Serialize(new
                {
                    timestamp = DateTimeOffset.UtcNow,
                    stage = safeStage,
                    code = safeCode,
                    durationMs = durationMilliseconds,
                });
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never interfere with activation.
        }
    }

    public static string CreateReport()
    {
        var builder = new StringBuilder();
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        builder.AppendLine($"Goose Launcher: {version}");
        builder.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        builder.AppendLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"Desktop activation protocol: {DesktopActivationProtocol.Version}");
        builder.AppendLine($"Process: {Environment.ProcessId}");
        builder.AppendLine();
        builder.AppendLine("Recent privacy-safe events (no prompts or paths):");
        try
        {
            string[] lines;
            lock (Gate) lines = File.Exists(LogPath) ? File.ReadAllLines(LogPath) : [];
            foreach (var line in lines.TakeLast(50)) builder.AppendLine(line);
        }
        catch (Exception error)
        {
            builder.AppendLine($"diagnostics_unavailable:{Sanitize(error.GetType().Name)}");
        }
        return builder.ToString();
    }

    private static string Sanitize(string value)
    {
        var result = SafeToken().Replace(value, "_");
        return result.Length <= 80 ? result : result[..80];
    }

    [GeneratedRegex("[^A-Za-z0-9_.-]")]
    private static partial Regex SafeToken();
}
