using Microsoft.Win32;
using SimpleBin.Helpers;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
        private readonly VolumeChangeTracker _volumeChanges = new();
        private bool _suspended;
        private bool _themeRestartAfterClear;
        private int _topologyChecksRemaining;
        private long _refreshDueTick;

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
            _eventTimer.Tick += (_, _) => OnRefreshTimer();
            TrayMenu.Opening += (_, _) =>
            {
                if (_isClearing) return;
                // An interactive click proves we are no longer suspended,
                // even if the OS's resume broadcast was missed.
                _suspended = false;
                _eventTimer.Stop();
                _topologyChecksRemaining = 0;
                if (CanQueryNow())
                    UpdateStatsControls();
                else
                    MarkStateUnavailable();
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

        // One-shot, event-triggered scheduling only; no periodic polling.
        // The earliest already scheduled refresh wins to avoid starvation.
        private void QueueRefresh(int delay = 750)
        {
            if (!_initialized || _isClearing || _suspended || IsDisposed) return;
            int remainingHold = _volumeChanges.RemainingHoldMilliseconds(DateTime.UtcNow);
            if (remainingHold > 0)
                delay = Math.Max(delay, remainingHold + 250);

            delay = Math.Clamp(delay, 1, 60_000);
            long due = Environment.TickCount64 + delay;
            if (_eventTimer.Enabled && _refreshDueTick <= due) return;
            _eventTimer.Stop();
            _refreshDueTick = due;
            _eventTimer.Interval = delay;
            _eventTimer.Start();
        }

        private bool CanQueryNow() =>
            !_suspended && !_isClearing &&
            _volumeChanges.RemainingHoldMilliseconds(DateTime.UtcNow) == 0;

        private void OnRefreshTimer()
        {
            _eventTimer.Stop();
            if (_isClearing || _suspended || IsDisposed) return;
            if (!CanQueryNow())
            {
                QueueRefresh(750);
                return;
            }

            UpdateStatsControls();
            if (_topologyChecksRemaining > 0)
            {
                --_topologyChecksRemaining;
                if (_topologyChecksRemaining > 0)
                    QueueRefresh(1800); // Up to three bounded checks per topology change.
            }
        }

        private void QueueTopologyRefresh()
        {
            _topologyChecksRemaining = Math.Max(_topologyChecksRemaining, 3);
            QueueRefresh(1100);
        }

        private void RecordVolumeEvent(VolumeEvent action, uint letters)
        {
            _volumeChanges.Observe(action, letters, DateTime.UtcNow);
            if (VolumeSnapshot.TryReadLetters(out uint current))
                _volumeChanges.ReconcileAssignedLetters(current);

            if (action is VolumeEvent.RemovalRequested or VolumeEvent.RemovalPending)
            {
                // Do not query a volume during a removal request. A bounded
                // grace period protects against missing/cancelled notifications.
                _eventTimer.Stop();
                ClearToolStripItem.Enabled = false;
                QueueRefresh((int)VolumeChangeTracker.RemovalGracePeriod.TotalMilliseconds + 250);
            }
            else
            {
                QueueTopologyRefresh();
            }
        }

        private static bool TryGetDeviceVolumeMask(IntPtr data, out uint letters)
        {
            letters = 0;
            if (data == IntPtr.Zero) return false;
            // DEV_BROADCAST_VOLUME: 12-byte header, unitmask at byte 12.
            int size = System.Runtime.InteropServices.Marshal.ReadInt32(data);
            if (size < 20 || size > 4096 ||
                System.Runtime.InteropServices.Marshal.ReadInt32(data, 4) != 2)
                return false;
            letters = unchecked((uint)System.Runtime.InteropServices.Marshal.ReadInt32(data, 12));
            return true;
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

        }

        private void ShowForm()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate(); // brings the window to the front if it's already open
            this.ShowInTaskbar = true;
        }

        private void MarkStateUnavailable()
        {
            SizeToolStripItem.Text = "Size: unavailable";
            ElementsToolStripItem.Text = "Items: unavailable";
            ClearToolStripItem.Enabled = false;
            TrayIcon.Icon = _iconHelper.GetIcon(); // Never falsely depict empty.
            TrayIcon.Text = "SimpleBin Safe - state unavailable";
        }

        private void UpdateStatsControls()
        {
            if (!CanQueryNow())
            {
                MarkStateUnavailable();
                return;
            }

            try
            {
                var before = VolumeSnapshot.Capture(_volumeChanges.Version);
                var (byteSize, itemCount) = BinHelper.GetBinSize();
                var after = VolumeSnapshot.Capture(_volumeChanges.Version);
                if (!before.Matches(after))
                    throw new InvalidOperationException(
                        "Mounted drives changed while reading Recycle Bin state.");

                SizeToolStripItem.Text = $"{SizeToolStripItem.Text?.Split()[0]} {ConvertSizeToString(byteSize)}";
                ElementsToolStripItem.Text = $"{ElementsToolStripItem.Text?.Split()[0]} {itemCount}";
                ClearToolStripItem.Enabled = itemCount > 0;
                TrayIcon.Icon = itemCount == 0
                    ? _iconHelper.GetEmptyIcon()
                    : _iconHelper.GetIcon();
                TrayIcon.Text = _shellMonitor.IsActive
                    ? "SimpleBin Safe" : "SimpleBin Safe - notifications unavailable";
            }
            catch
            {
                // Read errors, including a drive topology race, never mean empty.
                MarkStateUnavailable();
            }
        }

        private void SettingsToolStripItem_Click(object sender, EventArgs e) => ShowForm();

        private void ClearToolStripItem_Click(object sender, EventArgs e)
        {
            if (!CanQueryNow() || IsDisposed) return;

            VolumeSnapshot before;
            try
            {
                before = VolumeSnapshot.Capture(_volumeChanges.Version);
                var (_, count) = BinHelper.GetBinSize();
                var again = VolumeSnapshot.Capture(_volumeChanges.Version);
                if (!before.Matches(again))
                    throw new InvalidOperationException(
                        "A drive was connected or disconnected during verification.");
                if (count == 0)
                {
                    UpdateStatsControls();
                    return;
                }
            }
            catch (Exception ex)
            {
                MarkStateUnavailable();
                MessageBox.Show(
                    "Windows could not verify the Recycle Bin. Nothing was deleted.\n" + ex.Message,
                    "SimpleBin Safe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _isClearing = true;
            _eventTimer.Stop();
            _topologyChecksRemaining = 0;
            ClearToolStripItem.Enabled = false;

            try
            {
                // The ONLY destructive call. Windows Shell performs its own
                // confirmation/progress UI; no application confirmation added.
                int hr = BinHelper.ClearBin();
                const int cancelled = unchecked((int)0x800704C7);
                if (hr == cancelled) return;
                if (hr != 0)
                {
                    MessageBox.Show(
                        $"Windows did not report successful cleanup (HRESULT 0x{hr:X8}).",
                        "SimpleBin Safe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                VolumeSnapshot after = VolumeSnapshot.Capture(_volumeChanges.Version);
                if (!before.Matches(after))
                {
                    MessageBox.Show(
                        "The set of connected drives changed during cleanup. " +
                        "The Recycle Bins on all original drives could not be verified.",
                        "SimpleBin Safe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                long remaining = BinHelper.GetBinSize().itemCount;
                VolumeSnapshot verified = VolumeSnapshot.Capture(_volumeChanges.Version);
                if (!before.Matches(verified))
                {
                    MessageBox.Show(
                        "Connected drives changed during the final Recycle Bin check. " +
                        "Complete cleanup cannot be confirmed.",
                        "SimpleBin Safe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else if (remaining != 0)
                {
                    MessageBox.Show(
                        $"Windows reports {remaining} item(s) still present in accessible Recycle Bins.",
                        "SimpleBin Safe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Recycle Bin cleanup could not be fully verified.\n" + ex.Message,
                    "SimpleBin Safe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _isClearing = false;
                if (CanQueryNow())
                    UpdateStatsControls();
                else
                {
                    MarkStateUnavailable();
                    QueueTopologyRefresh();
                }

                // Never terminate the process during a Shell deletion operation,
                // even if a system theme change was delivered inside the native
                // Shell confirmation/progress message loop.
                if (_themeRestartAfterClear && !IsDisposed)
                {
                    _themeRestartAfterClear = false;
                    BeginInvoke(new Action(() =>
                        SaveFormStateAndRestart(needRecoverFormState: ShowInTaskbar)));
                }
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
                 m.Msg == ShellChangeMonitor.DriveMessage ||
                 m.Msg == ShellChangeMonitor.DesktopMessage))
            {
                bool received = _shellMonitor.ConsumeNotification(m.WParam, m.LParam);
                if (m.Msg == ShellChangeMonitor.DriveMessage)
                    RecordVolumeEvent(VolumeEvent.ShellDriveChanged, 0);
                else if (received)
                    QueueRefresh(450);
                // If notification data could not be read, schedule an
                // ordinary one-off resynchronization rather than trusting it.
                else
                    QueueRefresh(1000);
                return;
            }

            if (_initialized && m.Msg == WM_DEVICECHANGE)
            {
                long code = m.WParam.ToInt64();
                if (code == 0x0007) // DBT_DEVNODES_CHANGED
                {
                    RecordVolumeEvent(VolumeEvent.TopologyChanged, 0);
                }
                else if (code is 0x8000 or 0x8001 or 0x8002 or 0x8003 or 0x8004)
                {
                    // Ignore device-interface and HID events. Only actual
                    // DBT_DEVTYP_VOLUME messages may affect drive readiness.
                    // Virtual drives that omit this message are still handled
                    // through Shell drive changes and DBT_DEVNODES_CHANGED.
                    if (TryGetDeviceVolumeMask(m.LParam, out uint letters))
                    {
                        VolumeEvent action = code switch
                        {
                            0x8000 => VolumeEvent.Arrival,
                            0x8001 => VolumeEvent.RemovalRequested,
                            0x8002 => VolumeEvent.RemovalCancelled,
                            0x8003 => VolumeEvent.RemovalPending,
                            _ => VolumeEvent.RemovalCompleted
                        };
                        RecordVolumeEvent(action, letters);
                    }
                }
            }

            if (_initialized && m.Msg == WM_POWERBROADCAST)
            {
                if (m.WParam.ToInt64() == 0x0004) // PBT_APMSUSPEND
                {
                    _eventTimer.Stop();
                    _suspended = true;
                    ClearToolStripItem.Enabled = false;
                }
                else if (m.WParam.ToInt64() == 0x0012 ||
                         m.WParam.ToInt64() == 0x0007)
                {
                    _suspended = false;
                    RecordVolumeEvent(VolumeEvent.TopologyChanged, 0);
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
                        if (_isClearing)
                            _themeRestartAfterClear = true;
                        else
                            BeginInvoke(new Action(() =>
                                SaveFormStateAndRestart(needRecoverFormState: ShowInTaskbar)));
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
            try
            {
                StartupHelper.AddToStartup();
                AddToStartupBtn.Enabled = false;
                RemoveFromStartupBtn.Enabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to enable startup.\n" + ex.Message,
                    "SimpleBin Safe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void RemoveFromStartupBtn_Click(object sender, EventArgs e)
        {
            try
            {
                StartupHelper.RemoveFromStartup();
                AddToStartupBtn.Enabled = true;
                RemoveFromStartupBtn.Enabled = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to disable startup.\n" + ex.Message,
                    "SimpleBin Safe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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