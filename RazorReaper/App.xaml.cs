using System;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using RazorReaper.Services;

namespace RazorReaper
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var version = UpdateService.GetCurrentVersion();
            var window = new Window(new MainPage())
            {
                Title = $"Razor Reaper : Version {version}"
            };

            window.Created += async (_, __) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5));
                MainThread.BeginInvokeOnMainThread(UpdateService.CheckForUpdates);
            };

            return window;
        }
    }
}
