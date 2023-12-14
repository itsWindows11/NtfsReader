using System.Runtime.InteropServices;

namespace System.IO.Filesystem.Ntfs;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct INodeReference
{
    public uint InodeNumberLowPart;
    public ushort InodeNumberHighPart;
    public ushort SequenceNumber;
};