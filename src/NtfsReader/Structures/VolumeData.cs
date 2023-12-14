using System.Runtime.InteropServices;

namespace System.IO.Filesystem.Ntfs;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct VolumeData
{
    public ulong VolumeSerialNumber;
    public ulong NumberSectors;
    public ulong TotalClusters;
    public ulong FreeClusters;
    public ulong TotalReserved;
    public uint BytesPerSector;
    public uint BytesPerCluster;
    public uint BytesPerFileRecordSegment;
    public uint ClustersPerFileRecordSegment;
    public ulong MftValidDataLength;
    public ulong MftStartLcn;
    public ulong Mft2StartLcn;
    public ulong MftZoneStart;
    public ulong MftZoneEnd;
}