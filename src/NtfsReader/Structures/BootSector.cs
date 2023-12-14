using System.Runtime.InteropServices;

namespace System.IO.Filesystem.Ntfs;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
unsafe struct BootSector
{
    fixed byte AlignmentOrReserved1[3];
    public ulong Signature;
    public ushort BytesPerSector;
    public byte SectorsPerCluster;
    fixed byte AlignmentOrReserved2[26];
    public ulong TotalSectors;
    public ulong MftStartLcn;
    public ulong Mft2StartLcn;
    public uint ClustersPerMftRecord;
    public uint ClustersPerIndexRecord;
}