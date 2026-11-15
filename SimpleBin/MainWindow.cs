using Microsoft.Win32;
using SimpleBin.Helpers;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using SimpleBin.Helpers;
using static SimpleBin.Helpers.AppThemeHelper;

namespace SimpleBin
{
    public partial class MainWindow : Form
    {
        private readonly BinHelper _binHelper;
        private readonly IconHelper _iconHelper;
        private bool _isSystemDarkTheme;
        private bool _isRestarting;
        private readonly ShellChangeMonitor _shellMonitor = new();
        private readonly System.Windows.Forms.Timer _eventTimer;
        private bool _initialized;
        private bool _isClearing;
        private bool _removalPending;

        public MainWindow(BinHelper binHelper, IconHelper iconHelper)
        {
            var sysLang = CultureInfo.CurrentUICulture.Name;
            var appLang = "en-001";
            if (sysLang.Contains("ru")) appLang = "ru-Ru";
            if (sysLang.Contains("pl")) appLang = "pl-Pl";
            var culture = new CultureInfo(appLang);
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;

            InitializeComponent();

            InitializeThemeComboBox();

            _iconHelper = iconHelper;
            _isSystemDarkTheme = IsSystemDarkThemeEnabled();
            ThemeChanged += Form1_ThemeChanged;
            TrayMenu.RenderMode = ToolStripRenderMode.System;

            if (StartupHelper.IsInStartup())
            {
                AddToStartupBtn.Enabled = false;
                RemoveFromStartupBtn.Enabled = true;
            }
            else
            {
                AddToStartupBtn.Enabled = true;
                RemoveFromStartupBtn.Enabled = false;
            }

            _binHelper = binHelper;
            _eventTimer = new System.Windows.Forms.Timer { Interval = 750 };
            _eventTimer.Tick += (_, _) =>
            {
                _eventTimer.Stop();
                if (!_isClearing && !_removalPending) UpdateStatsControls();
            };
            TrayMenu.Opening += (_, _) =>
            {
                _eventTimer.Stop();
                if (!_isClearing && !_removalPending) UpdateStatsControls();
            };
            _initialized = true;
            UpdateStatsControls();

            this.Load += (_, _) =>
            {
                StartShellMonitoring();
                RecoverFormState();
            };
        }

        private void StartShellMonitoring()
        {
            if (!_initialized || !IsHandleCreated || _shellMonitor.IsActive) return;
            try
            {
                _shellMonitor.Start(Handle);
                UpdateStatsControls();
            }
            catch (Exception ex)
            {
                TrayIcon.Text = "SimpleBin Safe - notifications unavailable";
                MessageBox.Show(
                    "Shell notifications could not be registered.\n" + ex.Message +
                    "\nOpening the tray menu still refreshes the status.",
                    "SimpleBin Safe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void QueueRefresh(int delay = 750)
        {
            if (!_initialized || _isClearing || _removalPending || IsDisposed) return;
            _eventTimer.Stop();
            _eventTimer.Interval = delay;
            _eventTimer.Start();
        }

        private static bool IsVolumeNotification(IntPtr data)
        {
            if (data == IntPtr.Zero) return false;
            // DEV_BROADCAST_HDR: size, device type, reserved. DBT_DEVTYP_VOLUME = 2.
            return System.Runtime.InteropServices.Marshal.ReadInt32(data, 4) == 2;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_initialized) StartShellMonitoring();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            _shellMonitor.Stop();
            base.OnHandleDestroyed(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _eventTimer.Stop();
            _eventTimer.Dispose();
            _shellMonitor.Dispose();
            base.OnFormClosed(e);
        }

        private void Form1_ThemeChanged(bool isDarkTheme)
        {
            _iconHelper.SetTheme(isDarkTheme);
            UpdateStatsControls();
        }

        private delegate void ThemeHandler(bool isDarkTheme);
        private event ThemeHandler ThemeChanged;

        private void TrayIcon_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                Process.Start("explorer.exe", "shell:RecycleBinFolder");
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideForm();
            }
        }

        private void HideForm()
        {
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
            this.Hide();
            TrayIcon.Visible = true;

            //GC.Collect(2, GCCollectionMode.Forced);
            //GC.WaitForPendingFinalizers();

            //// 3. Î÷èñòêà êýøà GDI+
            //System.Drawing.Graphics.FromHwnd(IntPtr.Zero).Dispose();
        }

        private void ShowForm()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate(); // brings the window to the front if it's already open
            this.ShowInTaskbar = true;
        }

        private void UpdateStatsControls()
        {
            try
            {
                var (byteSize, itemCount) = BinHelper.GetBinSize();
                SizeToolStripItem.Text = $"{SizeToolStripItem.Text?.Split()[0]} {ConvertSizeToString(byteSize)}";
                ElementsToolStripItem.Text = $"{ElementsToolStripItem.Text?.Split()[0]} {itemCount}";
                ClearToolStripItem.Enabled = itemCount > 0 && !_isClearing && !_removalPending;
                TrayIcon.Icon = itemCount == 0
                    ? _iconHelper.GetEmptyIcon()
                    : _iconHelper.GetIcon();
                TrayIcon.Text = _shellMonitor.IsActive
                    ? "SimpleBin Safe"
                    : "SimpleBin Safe - notifications unavailable";
            }
            catch (Exception)
            {
                // Unknown is never treated as an empty Recycle Bin.
                SizeToolStripItem.Text = "Size: unavailable";
                ElementsToolStripItem.Text = "Items: unavailable";
                ClearToolStripItem.Enabled = false;
                TrayIcon.Icon = _iconHelper.GetIcon();
                TrayIcon.Text = "SimpleBin Safe - state unavailable";
            }
        }

        private void SettingsToolStripItem_Click(object sender, EventArgs e) => ShowForm();

        private void ClearToolStripItem_Click(object sender, EventArgs e)
        {
            if (_isClearing || _removalPending) return;
            _isClearing = true;
            _eventTimer.Stop();
            ClearToolStripItem.Enabled = false;

            try
            {
                // Exclusive deletion pathway: Windows Shell empties all available bins.
                int hr = BinHelper.ClearBin();
                const int Cancelled = unchecked((int)0x800704C7);
                if (hr < 0 && hr != Cancelled)
                {
                    MessageBox.Show(
                        $"Windows failed to empty all Recycle Bins (HRESULT 0x{hr:X8}).",
                        "SimpleBin Safe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "SimpleBin Safe",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _isClearing = false;
                if (!_removalPending) UpdateStatsControls();
            }
        }

        private static string ConvertSizeToString(long size) => size switch
        {
            < 1024 => $"{size} B",
            < 1024 * 1024 => $"{size / 1024f:F1} KB",
            < 1024 * 1024 * 1024 => $"{size / (1024f * 1024):F1} MB",
            _ => $"{size / (1024f * 1024 * 1024):F1} GB"
        };

        private void ExitToolStripItem_Click(object sender, EventArgs e)
        {
            this.FormClosing -= Form1_FormClosing!;
            this.Close();
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_SETTINGCHANGE = 0x001A;
            const int WM_DEVICECHANGE = 0x0219;
            const int WM_POWERBROADCAST = 0x0218;

            if (_initialized &&
                (m.Msg == ShellChangeMonitor.BinMessage ||
                 m.Msg == ShellChangeMonitor.DriveMessage))
            {
                if (_shellMonitor.ConsumeNotification(m.WParam, m.LParam))
                    QueueRefresh(m.Msg == ShellChangeMonitor.DriveMessage ? 1100 : 500);
                else
                    QueueRefresh(1100);
                return;
            }

            if (_initialized && m.Msg == WM_DEVICECHANGE)
            {
                long code = m.WParam.ToInt64();
                if (code == 0x0007) // DBT_DEVNODES_CHANGED: some virtual providers
                {
                    QueueRefresh(1100);
                }
                else if (IsVolumeNotification(m.LParam))
                {
                    if (code == 0x8001 || code == 0x8003) // QUERYREMOVE/PENDING
                    {
                        _removalPending = true;
                        _eventTimer.Stop();
                        ClearToolStripItem.Enabled = false;
                    }
                    else if (code == 0x8000 || code == 0x8004 || code == 0x8002)
                    {
                        // ARRIVAL, REMOVECOMPLETE or QUERYREMOVEFAILED
                        _removalPending = false;
                        QueueRefresh(1100);
                    }
                }
            }

            if (_initialized && m.Msg == WM_POWERBROADCAST)
            {
                if (m.WParam.ToInt64() == 0x0004) // PBT_APMSUSPEND
                {
                    _eventTimer.Stop();
                    _removalPending = true;
                    ClearToolStripItem.Enabled = false;
                }
                else if (m.WParam.ToInt64() == 0x0012 ||
                         m.WParam.ToInt64() == 0x0007) // resume automatic or interactive
                {
                    _removalPending = false;
                    QueueRefresh(1100);
                }
            }

            if (m.Msg == WM_SETTINGCHANGE && m.LParam != IntPtr.Zero)
            {
                bool currentTheme = IsSystemDarkThemeEnabled();
                if (_isSystemDarkTheme != currentTheme)
                {
                    _isSystemDarkTheme = currentTheme;
                    ThemeChanged?.Invoke(currentTheme);
                    if (AppThemeHelper.GetAppTheme() == Theme.System && !_isRestarting)
                    {
                        BeginInvoke(new Action(() =>
                            SaveFormStateAndRestart(needRecoverFormState: this.ShowInTaskbar)));
                    }
                }
            }

            base.WndProc(ref m);
        }

        public static bool IsSystemDarkThemeEnabled()
        {
            const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            const string valueName = "SystemUsesLightTheme";

            using var key = Registry.CurrentUser.OpenSubKey(keyPath);

            var keyValue = key?.GetValue(valueName);

            if (keyValue is null) return true; // If application can't open registry dark it will be use dark icons

            return (int)keyValue == 0;
        }

        private void AddToStartupBtn_Click(object sender, EventArgs e)
        {
            StartupHelper.AddToStartup();
            AddToStartupBtn.Enabled = false;
            RemoveFromStartupBtn.Enabled = true;
        }

        private void RemoveFromStartupBtn_Click(object sender, EventArgs e)
        {
            StartupHelper.RemoveFromStartup();
            AddToStartupBtn.Enabled = true;
            RemoveFromStartupBtn.Enabled = false;
        }

        private void InitializeThemeComboBox()
        {
            var resourceManager = new ComponentResourceManager(typeof(MainWindow));

            object[] themeComboBoxItems = [
                new KeyValuePair<Theme, string> (Theme.System, resourceManager.GetString("SystemTheme")!),
                new KeyValuePair<Theme, string> (Theme.Dark,   resourceManager.GetString("DarkTheme")!),
                new KeyValuePair<Theme, string> (Theme.Light,  resourceManager.GetString("LightTheme")!)
            ];

            ThemeComboBox.Items.AddRange(themeComboBoxItems);
            ThemeComboBox.DisplayMember = "Value";
            ThemeComboBox.ValueMember = "Key";

            var currentTheme = AppThemeHelper.GetAppTheme();
            ThemeComboBox.SelectedItem = themeComboBoxItems
                .Cast<KeyValuePair<Theme, string>>()
                .First(i => i.Key == currentTheme);
        }

        private void ThemeComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (ThemeComboBox.SelectedItem is KeyValuePair<Theme, string> pair)
            {
                AppThemeHelper.SetTheme(pair.Key);
                if (this.Visible) SaveFormStateAndRestart(needRecoverFormState: true);
            }
        }

        private void SaveFormStateAndRestart(bool needRecoverFormState)
        {
            _isRestarting = true;

            SettingsHelper.Save(s =>
            {
                s.Left = Left;
                s.Top = Top;
                s.Width = Width;
                s.Height = Height;
                s.IsNeedRecover = needRecoverFormState;
            });

            FormClosing -= Form1_FormClosing!;

            TrayIcon.Visible = false;
            TrayIcon.Dispose();

            Application.Restart();
            Environment.Exit(0);
        }

        private void RecoverFormState()
        {
            if (SettingsHelper.Get(s => s.IsNeedRecover, false))
            {
                StartPosition = FormStartPosition.Manual;
                this.Left = SettingsHelper.Get(s => s.Left, 0);
                this.Top = SettingsHelper.Get(s => s.Top, 0);
                this.Width = SettingsHelper.Get(s => s.Width, 0);
                this.Height = SettingsHelper.Get(s => s.Height, 0);
                SettingsHelper.Save(s => s.IsNeedRecover = false);
                ShowForm();
            }
            else
            {
                HideForm();
            }
        }

        private void supportLink_Click(object sender, EventArgs e) => Process.Start(new ProcessStartInfo
        {
            FileName = @"https://boosty.to/exalaolir/donate",
            UseShellExecute = true,
        });

        private void repoLink_Click(object sender, EventArgs e) => Process.Start(new ProcessStartInfo
        {
            FileName = @"https://github.com/exalaolir/SimpleBin",
            UseShellExecute = true,
        });
    }
}