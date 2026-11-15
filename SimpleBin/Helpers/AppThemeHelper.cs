using SimpleBin.Extensions;

namespace SimpleBin.Helpers
{
    public static class AppThemeHelper
    {
        public enum Theme : byte
        {
            System = 0,
            Dark,
            Light
        }

        public static void Initialize() =>
            Application.SetColorMode(GetAppTheme().ToSystemColorMode());

        public static Theme GetAppTheme()
        {
            byte stored = SettingsHelper.Get(s => s.Theme, (byte)Theme.System);
            return Enum.IsDefined(typeof(Theme), (Theme)stored)
                ? (Theme)stored : Theme.System;
        }

        public static void SetTheme(Theme theme)
        {
            if (!Enum.IsDefined(theme)) theme = Theme.System;
            SettingsHelper.Save(s => s.Theme = (byte)theme);
            Application.SetColorMode(theme.ToSystemColorMode());
        }
    }
}
