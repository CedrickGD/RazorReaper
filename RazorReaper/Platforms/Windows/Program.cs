namespace RazorReaper.WinUI;

/// <summary>
/// Custom entry point (the XAML-generated Main is disabled via DISABLE_XAML_GENERATED_MAIN
/// in the csproj). The only difference from the generated Main: the ARK-watch login mode is
/// routed off before any XAML/MAUI/WebView2 machinery is touched, so the watcher stays a
/// tiny headless process with no window.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (ArkWatch.ShouldRunWatchMode(args))
        {
            ArkWatch.Run();
            return;
        }

        // Headless self-test for the durability pipeline: runs the SHIPPING DurabilityReader
        // against a saved region screenshot and exits. This is how the reader is verified
        // against known ground truth without launching the game or the UI.
        if (FlakTest.ShouldRun(args))
        {
            Environment.Exit(FlakTest.Run(args));
            return;
        }

        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        global::Microsoft.UI.Xaml.Application.Start((p) =>
        {
            var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
