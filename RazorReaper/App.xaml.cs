using RazorReaper.Services;

namespace RazorReaper
{
    public partial class App : Application
    {
        public App(IFontInstaller fontInstaller)
        {
            InitializeComponent();
            _ = Task.Run(() => fontInstaller.EnsurePresetFontsInstalledAsync());
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
