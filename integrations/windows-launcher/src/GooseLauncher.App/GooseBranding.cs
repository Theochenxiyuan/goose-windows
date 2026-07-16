using Microsoft.UI.Windowing;

namespace GooseLauncher.App;

internal static class GooseBranding
{
    internal static string IconPath { get; } = Path.Combine(AppContext.BaseDirectory, "Goose.ico");

    internal static void ApplyIcon(AppWindow window) => window.SetIcon(IconPath);
}
