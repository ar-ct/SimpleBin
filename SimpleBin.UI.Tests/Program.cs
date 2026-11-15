using System.Reflection;
using System.Windows.Forms;
using SimpleBin;
using SimpleBin.Helpers;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            var resourceNames = typeof(IconHelper).Assembly.GetManifestResourceNames();
            Console.WriteLine("INFO: Embedded resources: " +
                string.Join(", ", resourceNames.Where(x => x.Contains("Icons"))));
            if (!resourceNames.Any(x => x == "SimpleBin.Resources.DarkIcons.resources") ||
                !resourceNames.Any(x => x == "SimpleBin.Resources.LightIcons.resources"))
                throw new InvalidOperationException("Original icon resources were not embedded.");

            var icons = new IconHelper(MainWindow.IsSystemDarkThemeEnabled());
            if (icons.GetIcon() is null || icons.GetEmptyIcon() is null)
                throw new InvalidOperationException("Default theme icons unavailable.");
            icons.SetTheme(true);
            if (icons.GetIcon() is null || icons.GetEmptyIcon() is null)
                throw new InvalidOperationException("Dark theme icons unavailable.");
            icons.SetTheme(false);
            if (icons.GetIcon() is null || icons.GetEmptyIcon() is null)
                throw new InvalidOperationException("Light theme icons unavailable.");
            Console.WriteLine("PASS: all original light/dark full/empty icon resources load");

            using var window = new MainWindow(
                new IconHelper(MainWindow.IsSystemDarkThemeEnabled()));
            // Creating the handle registers Shell notifications, while no
            // delete/empty action is ever invoked by this UI smoke test.
            IntPtr hwnd = window.Handle;
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("Tray UI window handle missing.");

            var trayField = typeof(MainWindow).GetField("TrayIcon",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (trayField?.GetValue(window) is not NotifyIcon tray)
                throw new InvalidOperationException("Tray notification object missing.");
            if (tray.Icon is null)
                throw new InvalidOperationException("Initial Recycle Bin icon not set.");

            Console.WriteLine("PASS: MainWindow constructed and initial bin icon was assigned");
            Console.WriteLine("PASS: native window handle created without modification of user files");
            window.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("UI SMOKE FAILURE: " + ex);
            return 1;
        }
    }
}
