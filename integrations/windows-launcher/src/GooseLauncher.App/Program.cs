using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace GooseLauncher.App;

public static class Program
{
    internal static string[] CommandLineArgs { get; private set; } = [];

    [STAThread]
    public static void Main(string[] args)
    {
        CommandLineArgs = args;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = initialization;
            new App();
        });
    }
}
