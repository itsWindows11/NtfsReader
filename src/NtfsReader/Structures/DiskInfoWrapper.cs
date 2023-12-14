namespace System.IO.Filesystem.Ntfs;

/// <summary>
/// Simple structure of available disk informations.
/// </summary>
internal sealed class DiskInfoWrapper : IDiskInfo
{
    public ushort BytesPerSector;
    public byte SectorsPerCluster;
    public ulong TotalSectors;
    public ulong MftStartLcn;
    public ulong Mft2StartLcn;
    public uint ClustersPerMftRecord;
    public uint ClustersPerIndexRecord;
    public ulong BytesPerMftRecord;
    public ulong BytesPerCluster;
    public ulong TotalClusters;

    ushort IDiskInfo.BytesPerSector => BytesPerSector;

    byte IDiskInfo.SectorsPerCluster => SectorsPerCluster;

    ulong IDiskInfo.TotalSectors => TotalSectors;

    ulong IDiskInfo.MftStartLcn => MftStartLcn;

    ulong IDiskInfo.Mft2StartLcn => Mft2StartLcn;

    uint IDiskInfo.ClustersPerMftRecord => ClustersPerMftRecord;

    uint IDiskInfo.ClustersPerIndexRecord => ClustersPerIndexRecord;

    ulong IDiskInfo.BytesPerMftRecord => BytesPerMftRecord;

    ulong IDiskInfo.BytesPerCluster => BytesPerCluster;

    ulong IDiskInfo.TotalClusters => TotalClusters;
}