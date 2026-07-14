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
        => Process.Start(installation.CreateInteractiveRunStartInfo(cwd, prompt));
}
