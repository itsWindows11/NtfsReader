using System.Runtime.InteropServices;

namespace System.IO.Filesystem.Ntfs;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct Fragment(ulong lcn, ulong nextVcn)
{
    public ulong Lcn = lcn;                // Logical cluster number, location on disk.
    public ulong NextVcn = nextVcn;        // Virtual cluster number of next fragment.
}