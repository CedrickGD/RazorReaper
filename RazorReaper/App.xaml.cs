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
            return new Window(new MainPage()) { Title = "RazorReaper Running: v1.0.0" };
        }
    }
}
