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
                Process.GetCurrentProcess().Kill();
                return;
            }

            this.InitializeComponent();
        }

        private static void BringExistingInstanceToFront()
        {
            var current = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                if (process.Id != current.Id && process.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(process.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(process.MainWindowHandle);
                    break;
                }
            }
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }
}
