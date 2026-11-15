using SimpleBin.Helpers;

namespace SimpleBin
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ApplicationConfiguration.Initialize();
            try
            {
                Application.Run(new MainWindow(
                    new IconHelper(MainWindow.IsSystemDarkThemeEnabled())));
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "SimpleBin Safe - Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
