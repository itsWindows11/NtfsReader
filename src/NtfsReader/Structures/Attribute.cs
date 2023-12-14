using System.Runtime.InteropServices;

namespace System.IO.Filesystem.Ntfs;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct Attribute
{
    public AttributeType AttributeType;
    public uint Length;
    public byte Nonresident;
    public byte NameLength;
    public ushort NameOffset;
    public ushort Flags;              /* 0x0001 = Compressed, 0x4000 = Encrypted, 0x8000 = Sparse */
    public ushort AttributeNumber;
}