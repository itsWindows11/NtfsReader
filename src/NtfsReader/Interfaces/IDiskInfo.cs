namespace System.IO.Filesystem.Ntfs;

/// <summary>
/// Exposes geometry and layout information about the scanned NTFS volume.
/// </summary>
public interface IDiskInfo
{
    /// <summary>Gets the number of bytes per physical sector.</summary>
    ushort BytesPerSector { get; }

    /// <summary>Gets the number of sectors per cluster.</summary>
    byte SectorsPerCluster { get; }

    /// <summary>Gets the total number of sectors on the volume.</summary>
    ulong TotalSectors { get; }

    /// <summary>
    /// Gets the Logical Cluster Number (LCN) of the first cluster of the primary MFT ($MFT).
    /// </summary>
    ulong MftStartLcn { get; }

    /// <summary>
    /// Gets the Logical Cluster Number (LCN) of the first cluster of the MFT mirror ($MFTMirr).
    /// </summary>
    ulong Mft2StartLcn { get; }

    /// <summary>Gets the number of clusters per MFT record.</summary>
    uint ClustersPerMftRecord { get; }

    /// <summary>Gets the number of clusters per index record.</summary>
    uint ClustersPerIndexRecord { get; }

    /// <summary>Gets the size of a single MFT record in bytes.</summary>
    ulong BytesPerMftRecord { get; }

    /// <summary>Gets the size of a cluster in bytes.</summary>
    ulong BytesPerCluster { get; }

    /// <summary>Gets the total number of clusters on the volume.</summary>
    ulong TotalClusters { get; }
}
