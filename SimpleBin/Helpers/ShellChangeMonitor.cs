using System.Runtime.InteropServices;

namespace SimpleBin.Helpers
{
    /// <summary>
    /// Event-only Shell notifications. No persistent directory watching, no enumeration
    /// of $Recycle.Bin directories and no open directory handles on a drive.
    /// </summary>
    internal sealed class ShellChangeMonitor : IDisposable
    {
        internal const int BinMessage = 0x8000 + 0x231;
        internal const int DriveMessage = 0x8000 + 0x232;
        internal const int DesktopMessage = 0x8000 + 0x233;

        private const int Sources = 0x0001 | 0x0002 | 0x8000;
        // Windows may report Recycle Bin changes as UPDATEITEM, UPDATEDIR,
        // or a change to the Recycle Bin's items rather than the bin root.
        private const int BinEvents = 0x7FFFFFFF;
        private const int DriveEvents =
            0x00000020 | 0x00000040 | 0x00000080 | 0x00000100 |
            0x00001000 | 0x00002000 | 0x00010000;
        // Recycle Bin is also a shell item displayed on the desktop.
        // Desktop scope is NOT recursive: unrelated deep file activity
        // does not flood the application with notifications.
        private const int DesktopEvents =
            0x00000001 | // SHCNE_RENAMEITEM
            0x00000004 | // SHCNE_DELETE
            0x00000010 | // SHCNE_RMDIR
            0x00001000 | // SHCNE_UPDATEDIR (coalesced events)
            0x00002000 | // SHCNE_UPDATEITEM
            0x00008000;  // SHCNE_UPDATEIMAGE

        private static readonly Guid RecycleBinFolder =
            new("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
        private static readonly Guid ComputerFolder =
            new("0AC0837C-BBF8-452A-850D-79D08E667CA7");
        private static readonly Guid DesktopFolder =
            new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

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
        private uint _desktopRegistration;
        private IntPtr _binPidl;
        private IntPtr _computerPidl;
        private IntPtr _desktopPidl;

        internal bool IsActive => _binRegistration != 0 && _driveRegistration != 0;
        internal bool HasDesktopFallback => _desktopRegistration != 0;

        internal void Start(IntPtr windowHandle)
        {
            if (IsActive) return;
            if (windowHandle == IntPtr.Zero)
                throw new ArgumentException("Valid window handle required.", nameof(windowHandle));

            try
            {
                _binPidl = GetPidl(RecycleBinFolder, "Recycle Bin");
                _computerPidl = GetPidl(ComputerFolder, "Computer");
                _binRegistration = Register(windowHandle, BinEvents, BinMessage,
                    _binPidl, recursive: true);
                _driveRegistration = Register(windowHandle, DriveEvents, DriveMessage,
                    _computerPidl, recursive: false);

                // Optional redundancy for desktop Recycle Bin icon updates.
                // Registration failures here must not disable the main monitors.
                int hr = SHGetKnownFolderIDList(in DesktopFolder, 0, IntPtr.Zero,
                    out _desktopPidl);
                if (hr == 0 && _desktopPidl != IntPtr.Zero)
                    _desktopRegistration = TryRegister(windowHandle, DesktopEvents,
                        DesktopMessage, _desktopPidl, recursive: true);
            }
            catch
            {
                Stop();
                throw;
            }
        }

        private static IntPtr GetPidl(Guid id, string name)
        {
            int hr = SHGetKnownFolderIDList(in id, 0, IntPtr.Zero, out IntPtr pidl);
            if (hr != 0 || pidl == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"{name} shell ID unavailable (HRESULT 0x{hr:X8}).");
            return pidl;
        }

        private static uint TryRegister(IntPtr hwnd, int events, int message,
            IntPtr pidl, bool recursive) =>
            SHChangeNotifyRegister(hwnd, Sources, events, (uint)message, 1,
                [new ChangeNotifyEntry { Pidl = pidl, Recursive = recursive }]);

        private static uint Register(IntPtr hwnd, int events, int message,
            IntPtr pidl, bool recursive)
        {
            uint id = TryRegister(hwnd, events, message, pidl, recursive);
            if (id == 0)
                throw new InvalidOperationException(
                    $"Shell notification registration failed (message 0x{message:X}).");
            return id;
        }

        // SHCNRF_NewDelivery: wParam is hChange, lParam is process ID.
        // Locking and unlocking is mandatory for successfully delivered data.
        internal bool ConsumeNotification(IntPtr wParam, IntPtr lParam) =>
            ConsumeNotification(wParam, lParam, out _);

        internal bool ConsumeNotification(IntPtr wParam, IntPtr lParam, out int eventId)
        {
            eventId = 0;
            IntPtr locked = SHChangeNotification_Lock(
                wParam, unchecked((uint)lParam.ToInt64()), out _, out eventId);
            if (locked == IntPtr.Zero) return false;
            try { return true; }
            finally { SHChangeNotification_Unlock(locked); }
        }

        internal void Stop()
        {
            if (_desktopRegistration != 0)
            {
                SHChangeNotifyDeregister(_desktopRegistration);
                _desktopRegistration = 0;
            }
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
            if (_desktopPidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(_desktopPidl);
                _desktopPidl = IntPtr.Zero;
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
