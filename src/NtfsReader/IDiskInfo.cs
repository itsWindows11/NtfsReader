namespace System.IO.Filesystem.Ntfs;

/// <summary>
/// Disk information
/// </summary>
public interface IDiskInfo
{
    ushort BytesPerSector { get; }

    byte SectorsPerCluster { get; }

    ulong TotalSectors { get; }

    ulong MftStartLcn { get; }

    ulong Mft2StartLcn { get; }

    uint ClustersPerMftRecord { get; }

    uint ClustersPerIndexRecord { get; }

    ulong BytesPerMftRecord { get; }

    ulong BytesPerCluster { get; }

    ulong TotalClusters { get; }
}
