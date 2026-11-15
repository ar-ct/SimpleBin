using SimpleBin.Properties;

namespace SimpleBin.Helpers
{
    internal static class SettingsHelper
    {
        internal static T Get<T>(Func<Settings, T> getter, T fallback)
        {
            try
            {
                return getter(Settings.Default);
            }
            catch
            {
                // A failed read must NEVER reset or overwrite all user settings.
                return fallback;
            }
        }

        internal static void Save(Action<Settings> setter)
        {
            try
            {
                setter(Settings.Default);
                Settings.Default.Save();
            }
            catch
            {
                MessageBox.Show("Unable to save SimpleBin Safe preferences.",
                    "SimpleBin Safe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
