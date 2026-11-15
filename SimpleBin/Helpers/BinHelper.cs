using System.Runtime.InteropServices;

namespace SimpleBin
{
    // All deletion is delegated to Windows Shell. Never enumerate or delete
    // $Recycle.Bin directories, and never open handles on mounted volumes.
    internal static partial class NativeBinMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Shqueryrbinfo
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int SHQueryRecycleBinW(
            string? pszRootPath, ref Shqueryrbinfo info);

        [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int SHEmptyRecycleBinW(
            IntPtr hwnd, string? pszRootPath, uint flags);
    }

    public sealed class BinHelper
    {
        internal static (long byteSize, long itemCount) GetBinSize()
        {
            var info = new NativeBinMethods.Shqueryrbinfo
            {
                cbSize = Marshal.SizeOf<NativeBinMethods.Shqueryrbinfo>()
            };
            int hr = NativeBinMethods.SHQueryRecycleBinW(null, ref info);
            if (hr != 0)
                throw new COMException($"Recycle Bin query failed (HRESULT 0x{hr:X8}).", hr);

            if (info.i64NumItems < 0 || info.i64Size < 0)
                throw new InvalidOperationException("Windows returned invalid Recycle Bin counts.");

            return (info.i64Size, info.i64NumItems);
        }

        // This is the application's ONLY operation that deletes user data.
        // Null selects every currently available Shell-supported recycle bin.
        // Zero flags preserve Windows native confirmation and progress dialogs.
        internal static int ClearBin() =>
            NativeBinMethods.SHEmptyRecycleBinW(IntPtr.Zero, null, 0);
    }
}
