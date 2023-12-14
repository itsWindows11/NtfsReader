using System.Runtime.InteropServices;

namespace System.IO.Filesystem.Ntfs;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
unsafe struct AttributeList
{
    public AttributeType AttributeType;
    public ushort Length;
    public byte NameLength;
    public byte NameOffset;
    public ulong LowestVcn;
    public INodeReference FileReferenceNumber;
    public ushort Instance;
    public fixed ushort AlignmentOrReserved[3];
};