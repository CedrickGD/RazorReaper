using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RazorReaper.WinUI
{
    public partial class App : MauiWinUIApplication
    {
        private static Mutex? _mutex;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        public App()
        {
            _mutex = new Mutex(true, "RazorReaper_SingleInstance_Mutex", out bool isNewInstance);

            if (!isNewInstance)
            {
                BringExistingInstanceToFront();
                Environment.Exit(0);
                return;
            }

            this.InitializeComponent();
        }

        private static void BringExistingInstanceToFront()
        {
            using var current = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName(current.ProcessName);
            try
            {
                foreach (var process in processes)
                {
                    if (process.Id != current.Id && process.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(process.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(process.MainWindowHandle);
                        break;
                    }
                }
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }
}
