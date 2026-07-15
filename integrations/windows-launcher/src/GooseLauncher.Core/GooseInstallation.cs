using System.Diagnostics;

namespace GooseLauncher.Core;

public sealed record GooseInstallation(string CliPath, string DesktopPath)
{
    public static GooseInstallation? Locate()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("GOOSE_WINDOWS_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return LocateProductRoot(configuredRoot);

        var launcherDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var productRoot = Directory.GetParent(launcherDirectory)?.FullName;
        return productRoot is null ? null : LocateProductRoot(productRoot);
    }

    internal static GooseInstallation? LocateProductRoot(string productRoot)
    {
        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(productRoot));
        var desktop = Path.Combine(root, "Goose.exe");
        var cli = Path.Combine(root, "resources", "bin", "goose.exe");
        return File.Exists(desktop) && File.Exists(cli)
            ? new GooseInstallation(cli, desktop)
            : null;
    }

    public ProcessStartInfo CreateDesktopStartInfo(bool forLauncherActivation = false)
    {
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
}
