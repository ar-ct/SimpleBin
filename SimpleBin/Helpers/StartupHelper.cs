using Microsoft.Win32;

namespace SimpleBin.Helpers
{
    internal static class StartupHelper
    {
        private const string AppName = "SimpleBinSafe";
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        internal static bool IsInStartup()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return !string.IsNullOrWhiteSpace(key?.GetValue(AppName) as string);
        }

        // This user-registry change happens only after an explicit click.
        internal static void AddToStartup()
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null)
                throw new InvalidOperationException("Windows denied access to the user startup key.");
            key.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
        }

        internal static void RemoveFromStartup()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.DeleteValue(AppName, false);
        }
    }
}
