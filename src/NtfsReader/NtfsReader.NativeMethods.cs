using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace System.IO.Filesystem.Ntfs;

public partial class NtfsReader
{
    [DllImport("kernel32", CharSet = CharSet.Auto, BestFitMapping = false)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeName,
        StringBuilder uniqueVolumeName,
        int uniqueNameBufferCapacity
    );

    [DllImport("kernel32", CharSet = CharSet.Auto, BestFitMapping = false)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        FileAccess fileAccess,
        FileShare fileShare,
        IntPtr lpSecurityAttributes,
        FileMode fileMode,
        int dwFlagsAndAttributes,
        IntPtr hTemplateFile
    );

    [DllImport("kernel32", CharSet = CharSet.Auto)]
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        IntPtr lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        ref NativeOverlapped lpOverlapped
    );

    [Serializable]
    private enum FileMode : int
    {
        Append = 6,
        Create = 2,
        CreateNew = 1,
        Open = 3,
        OpenOrCreate = 4,
        Truncate = 5
    }

    [Serializable, Flags]
    private enum FileShare : int
    {
        None = 0,
        Read = 1,
        Write = 2,
        Delete = 4,
        All = Read | Write | Delete
    }

    [Serializable, Flags]
    private enum FileAccess : int
    {
        Read = 1,
        ReadWrite = 3,
        Write = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlapped(ulong offset)
    {
        public IntPtr privateLow = IntPtr.Zero;
        public IntPtr privateHigh = IntPtr.Zero;
        public ulong Offset = offset;
        public IntPtr EventHandle = IntPtr.Zero;
    }
}
