using System.Diagnostics;
using Microsoft.Win32;

namespace GooseLauncher.Core;

public sealed record GooseInstallation(string CliPath, string? DesktopPath)
{
    public static GooseInstallation? Locate(string? cliOverride = null, string? desktopOverride = null)
    {
        var configuredCli = NormalizeOverride(cliOverride);
        if (configuredCli is not null && !File.Exists(configuredCli)) return null;

        var configuredDesktop = NormalizeOverride(desktopOverride);
        var desktopOverrideIsValid = configuredDesktop is not null && File.Exists(configuredDesktop);
        var bundledDesktop = Path.Combine(AppContext.BaseDirectory, "Goose.exe");
        var bundledCli = Path.Combine(AppContext.BaseDirectory, "resources", "bin", "goose.exe");
        var desktopCandidates = new[]
        {
            desktopOverrideIsValid ? configuredDesktop : null,
            configuredDesktop is null ? bundledDesktop : null,
            configuredDesktop is null ? FindDesktopFromProtocol() : null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Goose", "Goose.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Goose", "Goose.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Goose", "Goose.exe")
        };
        var explicitCli = Environment.GetEnvironmentVariable("GOOSE_CLI_PATH");
        var configuredDesktopCli = desktopOverrideIsValid
            ? Path.Combine(Path.GetDirectoryName(configuredDesktop!)!, "resources", "bin", "goose.exe")
            : null;
        var cliCandidates = new List<string?>
        {
            configuredCli,
            configuredDesktopCli,
            bundledCli,
            explicitCli,
            FindOnPath("goose.exe"),
            FindOnPath("goose"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "goose.exe")
        };
        foreach (var desktop in desktopCandidates)
        {
            if (!string.IsNullOrWhiteSpace(desktop)) cliCandidates.Add(Path.Combine(Path.GetDirectoryName(desktop)!, "resources", "bin", "goose.exe"));
        }
        var cli = cliCandidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        if (cli is null) return null;
        var desktopPath = configuredDesktop is not null
            ? (desktopOverrideIsValid ? configuredDesktop : null)
            : desktopCandidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        return new GooseInstallation(Path.GetFullPath(cli), desktopPath);
    }

    private static string? NormalizeOverride(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))); }
        catch { return path.Trim(); }
    }

    public ProcessStartInfo CreateDesktopStartInfo(bool forLauncherActivation = false)
    {
        if (DesktopPath is null) throw new FileNotFoundException("Goose Desktop was not found.");
        var startInfo = new ProcessStartInfo(DesktopPath) { UseShellExecute = true };
        if (forLauncherActivation) startInfo.ArgumentList.Add("--launcher-activation");
        return startInfo;
    }

    public ProcessStartInfo CreateInteractiveRunStartInfo(string cwd, string prompt)
    {
        var startInfo = CreateInteractiveStartInfo(cwd);
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--text");
        startInfo.ArgumentList.Add(prompt);
        startInfo.ArgumentList.Add("--interactive");
        return startInfo;
    }

    public ProcessStartInfo CreateInteractiveSessionStartInfo(string cwd)
    {
        var startInfo = CreateInteractiveStartInfo(cwd);
        startInfo.ArgumentList.Add("session");
        return startInfo;
    }

    private ProcessStartInfo CreateInteractiveStartInfo(string cwd) => new(CliPath)
    {
        UseShellExecute = true,
        WorkingDirectory = Path.GetFullPath(cwd),
    };

    private static string? FindOnPath(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    private static string? FindDesktopFromProtocol()
    {
        try
        {
            var command = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\goose\shell\open\command", null, null) as string
                ?? Registry.GetValue(@"HKEY_CLASSES_ROOT\goose\shell\open\command", null, null) as string;
            if (string.IsNullOrWhiteSpace(command)) return null;
            if (command[0] == '"')
            {
                var closingQuote = command.IndexOf('"', 1);
                return closingQuote > 1 ? command[1..closingQuote] : null;
            }
            var firstSpace = command.IndexOf(' ');
            return firstSpace > 0 ? command[..firstSpace] : command;
        }
        catch { return null; }
    }
}
