using System.Diagnostics;
using GooseLauncher.Core;

namespace GooseLauncher.App;

internal static class GooseProcessLauncher
{
    internal static void OpenDesktop(GooseInstallation installation, string uri)
    {
        ProcessStartInfo startInfo;
        if (!string.IsNullOrWhiteSpace(installation.DesktopPath))
        {
            startInfo = new ProcessStartInfo(installation.DesktopPath) { UseShellExecute = true };
            startInfo.ArgumentList.Add(uri);
        }
        else
        {
            startInfo = new ProcessStartInfo(uri) { UseShellExecute = true };
        }

        Process.Start(startInfo);
    }

    internal static void OpenTerminal(GooseInstallation installation, string cwd, string prompt)
    {
        var terminal = FindWindowsTerminal();
        ProcessStartInfo startInfo;
        if (terminal is not null)
        {
            startInfo = new ProcessStartInfo(terminal) { UseShellExecute = true };
            startInfo.ArgumentList.Add("new-tab");
            startInfo.ArgumentList.Add("--startingDirectory");
            startInfo.ArgumentList.Add(Path.GetFullPath(cwd));
            startInfo.ArgumentList.Add(installation.CliPath);
        }
        else
        {
            startInfo = new ProcessStartInfo(installation.CliPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetFullPath(cwd),
            };
        }

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--text");
        startInfo.ArgumentList.Add(prompt);
        startInfo.ArgumentList.Add("--interactive");
        Process.Start(startInfo);
    }

    private static string? FindWindowsTerminal()
    {
        var appAlias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "wt.exe");
        if (File.Exists(appAlias)) return appAlias;

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), "wt.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        return null;
    }
}
