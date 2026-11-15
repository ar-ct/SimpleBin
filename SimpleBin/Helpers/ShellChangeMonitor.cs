using System.Runtime.InteropServices;

namespace SimpleBin.Helpers
{
    /// <summary>
    /// Event-only Shell notifications. No directory enumeration, file system
    /// watchers or persistent handles to any user volume, including VeraCrypt.
    /// </summary>
    internal sealed class ShellChangeMonitor : IDisposable
    {
        internal const int BinMessage = 0x8000 + 0x231;   // WM_APP
        internal const int DriveMessage = 0x8000 + 0x232;

        private const int Sources = 0x0001 | 0x0002 | 0x8000;
        private const int BinEvents = 0x7FFFFFFF;
        private const int DriveEvents =
            0x00000020 | // SHCNE_MEDIAINSERTED
            0x00000040 | // SHCNE_MEDIAREMOVED
            0x00000080 | // SHCNE_DRIVEREMOVED
            0x00000100 | // SHCNE_DRIVEADD
            0x00001000 | // SHCNE_UPDATEDIR
            0x00002000 | // SHCNE_UPDATEITEM
            0x00010000;  // SHCNE_DRIVEADDGUI

        private static readonly Guid RecycleBinFolder =
            new("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
        private static readonly Guid ComputerFolder =
            new("0AC0837C-BBF8-452A-850D-79D08E667CA7");

        [StructLayout(LayoutKind.Sequential)]
        private struct ChangeNotifyEntry
        {
            public IntPtr Pidl;
            [MarshalAs(UnmanagedType.Bool)]
            public bool Recursive;
        }

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHGetKnownFolderIDList(
            in Guid folderId, uint flags, IntPtr token, out IntPtr pidl);

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern uint SHChangeNotifyRegister(
            IntPtr hwnd, int sources, int events, uint message,
            int entryCount, [In] ChangeNotifyEntry[] entries);

        [DllImport("shell32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SHChangeNotifyDeregister(uint registrationId);

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern IntPtr SHChangeNotification_Lock(
            IntPtr change, uint processId, out IntPtr pidlArray, out int eventId);

        [DllImport("shell32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SHChangeNotification_Unlock(IntPtr lockedNotification);

        private uint _binRegistration;
        private uint _driveRegistration;
        private IntPtr _binPidl;
        private IntPtr _computerPidl;

        public bool IsActive => _binRegistration != 0 && _driveRegistration != 0;

        public void Start(IntPtr windowHandle)
        {
            if (IsActive) return;
            if (windowHandle == IntPtr.Zero)
                throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));

            try
            {
                int hr = SHGetKnownFolderIDList(in RecycleBinFolder, 0, IntPtr.Zero, out _binPidl);
                if (hr != 0 || _binPidl == IntPtr.Zero)
                    throw new InvalidOperationException($"Recycle Bin PIDL unavailable: 0x{hr:X8}");

                hr = SHGetKnownFolderIDList(in ComputerFolder, 0, IntPtr.Zero, out _computerPidl);
                if (hr != 0 || _computerPidl == IntPtr.Zero)
                    throw new InvalidOperationException($"Computer PIDL unavailable: 0x{hr:X8}");

                // Microsoft documents that cEntries must be ONE for reliable
                // deregistration. Two separate registrations, one PIDL each.
                _binRegistration = Register(windowHandle, BinEvents, BinMessage,
                    _binPidl, true);
                _driveRegistration = Register(windowHandle, DriveEvents, DriveMessage,
                    _computerPidl, false);
            }
            catch
            {
                Stop();
                throw;
            }
        }

        private static uint Register(IntPtr hwnd, int events, int message, IntPtr pidl, bool recursive)
        {
            var entry = new[] { new ChangeNotifyEntry { Pidl = pidl, Recursive = recursive } };
            uint id = SHChangeNotifyRegister(hwnd, Sources, events, (uint)message, 1, entry);
            if (id == 0)
                throw new InvalidOperationException(
                    $"Shell change notification registration failed (message 0x{message:X}).");
            return id;
        }

        // With SHCNRF_NewDelivery, even ignored events must be locked and unlocked.
        public bool ConsumeNotification(IntPtr wParam, IntPtr lParam)
        {
            if (!IsActive) return false;
            var handle = SHChangeNotification_Lock(
                wParam, unchecked((uint)lParam.ToInt64()), out _, out _);
            if (handle == IntPtr.Zero) return false;
            try { return true; }
            finally { SHChangeNotification_Unlock(handle); }
        }

        public void Stop()
        {
            if (_driveRegistration != 0)
            {
                SHChangeNotifyDeregister(_driveRegistration);
                _driveRegistration = 0;
            }
            if (_binRegistration != 0)
            {
                SHChangeNotifyDeregister(_binRegistration);
                _binRegistration = 0;
            }
            if (_computerPidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(_computerPidl);
                _computerPidl = IntPtr.Zero;
            }
            if (_binPidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(_binPidl);
                _binPidl = IntPtr.Zero;
            }
        }

        public void Dispose() => Stop();
    }
}
