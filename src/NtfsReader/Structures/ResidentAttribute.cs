using System.Runtime.InteropServices;

namespace System.IO.Filesystem.Ntfs;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct ResidentAttribute
{
    public Attribute Attribute;
    public uint ValueLength;
    public ushort ValueOffset;
    public ushort Flags;               // 0x0001 = Indexed
};