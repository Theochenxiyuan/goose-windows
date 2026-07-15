using System.Diagnostics;
using GooseLauncher.Core;

namespace GooseLauncher.App;

internal static class GooseProcessLauncher
{
    internal static void OpenDesktop(GooseInstallation installation) =>
        Process.Start(installation.CreateDesktopStartInfo());

    internal static void OpenTerminal(
        GooseInstallation installation,
        string cwd,
        string prompt,
        LauncherSessionSelection? sessionSelection)
        => Process.Start(installation.CreateInteractiveRunStartInfo(cwd, prompt, sessionSelection));

    internal static void OpenTerminalSession(GooseInstallation installation, string cwd)
        => Process.Start(installation.CreateInteractiveSessionStartInfo(cwd));
}
