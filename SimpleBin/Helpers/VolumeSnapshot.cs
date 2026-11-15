using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SimpleBin.Helpers
{
    // GetLogicalDrives reads the operating system's assigned-letter bitmap.
    // It does not enumerate $Recycle.Bin, read file contents or retain a
    // directory handle on an encrypted volume.
    internal readonly record struct VolumeSnapshot(uint AssignedLetters, long EventVersion)
    {
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern uint GetLogicalDrives();

        internal static VolumeSnapshot Capture(long eventVersion)
        {
            uint letters = GetLogicalDrives();
            if (letters == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows could not enumerate assigned drive letters.");
            return new VolumeSnapshot(letters, eventVersion);
        }

        internal static bool TryReadLetters(out uint letters)
        {
            letters = GetLogicalDrives();
            return letters != 0;
        }

        internal bool Matches(VolumeSnapshot other) =>
            AssignedLetters == other.AssignedLetters &&
            EventVersion == other.EventVersion;
    }
}
