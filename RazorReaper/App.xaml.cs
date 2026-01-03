using System;
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

            return window;
        }
    }
}
