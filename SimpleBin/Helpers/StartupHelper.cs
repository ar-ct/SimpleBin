using Microsoft.Win32;

namespace SimpleBin.Helpers
{
    internal static class StartupHelper
    {
        private const string AppName = "SimpleBinSafe";
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        public static bool IsInStartup()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return !string.IsNullOrWhiteSpace(key?.GetValue(AppName) as string);
        }

        public static void AddToStartup()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
        }

        public static void RemoveFromStartup()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.DeleteValue(AppName, false);
        }
    }
}
