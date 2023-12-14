using System.Runtime.InteropServices;

namespace System.IO.Filesystem.Ntfs;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
unsafe struct NonResidentAttribute
{
    public Attribute Attribute;
    public ulong StartingVcn;
    public ulong LastVcn;
    public ushort RunArrayOffset;
    public byte CompressionUnit;
    public fixed byte AlignmentOrReserved[5];
    public ulong AllocatedSize;
    public ulong DataSize;
    public ulong InitializedSize;
    public ulong CompressedSize;    // Only when compressed
};