using Windows.ApplicationModel;

namespace GooseLauncher.App;

internal static class StartupRegistration
{
    internal const string TaskId = "GooseLauncherStartup";

    internal static async Task<bool> IsEnabledAsync()
    {
        var task = await StartupTask.GetAsync(TaskId);
        return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
    }

    internal static async Task SetEnabledAsync(bool enabled)
    {
        var task = await StartupTask.GetAsync(TaskId);
        if (!enabled)
        {
            task.Disable();
            return;
        }

        var state = await task.RequestEnableAsync();
        if (state is not (StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy))
            throw new InvalidOperationException(state == StartupTaskState.DisabledByUser
                ? Strings.Get(
                    "Windows 已阻止此启动项。请在 Windows 设置的“启动应用”中启用 Goose。",
                    "Windows has blocked this startup item. Enable Goose in Windows Startup Apps settings.")
                : Strings.Get("无法启用 Windows 启动项。", "Could not enable the Windows startup item."));
    }
}
