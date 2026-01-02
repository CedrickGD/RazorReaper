using System.Reflection;
using AutoUpdaterDotNET;

namespace RazorReaper.Services
{
    public static class UpdateService
    {
        public static string GetCurrentVersion()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            if (version == null)
            {
                return "0.0.0";
            }

            return $"{version.Major}.{version.Minor}.{version.Build}";
        }

        public static void CheckForUpdates()
        {
            AutoUpdater.ShowSkipButton = true;
            AutoUpdater.ShowRemindLaterButton = true;
            AutoUpdater.LetUserSelectRemindLater = true;
            AutoUpdater.RemindLaterTimeSpan = RemindLaterFormat.Days;
            AutoUpdater.RemindLaterAt = 1;
            AutoUpdater.RunUpdateAsAdmin = false;
            AutoUpdater.ReportErrors = true;
            AutoUpdater.Start("https://raw.githubusercontent.com/CedrickGD/RazorReaper/main/update.xml");
        }
    }
}
