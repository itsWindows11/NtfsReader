using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace System.IO.Filesystem.Ntfs;

/// <summary>
/// Ntfs metadata reader.
/// 
/// This class is used to get files & directories information of an NTFS volume.
/// This is a lot faster than using conventional directory browsing method
/// particularly when browsing really big directories.
/// </summary>
/// <remarks>Admnistrator rights are required in order to use this method.</remarks>
public sealed partial class NtfsReader
{
    /// <summary>
    /// NtfsReader constructor.
    /// </summary>
    /// <param name="driveInfo">The drive you want to read metadata from.</param>
    /// <param name="include">Information to retrieve from each node while scanning the disk</param>
    /// <remarks>Streams & Fragments are expensive to store in memory, if you don't need them, don't retrieve them.</remarks>
    public NtfsReader(DriveInfo driveInfo, RetrieveMode retrieveMode)
    {
        _driveInfo = driveInfo ?? throw new ArgumentNullException("driveInfo");
        _retrieveMode = retrieveMode;

        var builder = new StringBuilder(1024);
        GetVolumeNameForVolumeMountPoint(_driveInfo.RootDirectory.Name, builder, builder.Capacity);

        string volume = builder.ToString().TrimEnd(['\\']);

        _volumeHandle =
            CreateFile(
                volume,
                FileAccess.Read,
                FileShare.All,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero
                );

        if (_volumeHandle == null || _volumeHandle.IsInvalid)
            throw new IOException(
                string.Format(
                    "Unable to open volume {0}. Make sure it exists and that you have Administrator privileges.",
                    driveInfo
                )
            );

        // TODO: Move this code to a separate async method.
        using (_volumeHandle)
        {
            InitializeDiskInfo();
            _nodes = ProcessMft();
        }

        _nameIndex = null;
        _volumeHandle = null;

        GC.Collect();
    }

    public IDiskInfo DiskInfo => _diskInfo;

    /// <summary>
    /// Get all nodes under the specified rootPath.
    /// </summary>
    /// <param name="rootPath">
    /// The rootPath must at least contain the drive
    /// and may include any number of subdirectories.
    /// Wildcards aren't supported.
    /// </param>
    public List<INode> GetNodes(string rootPath)
    {
        var nodes = new List<INode>();

        uint nodeCount = (uint)_nodes.Length;

        Parallel.For(0, nodeCount, i =>
        {
            if (_nodes[i].NameIndex != 0
                && GetNodeFullNameCore((uint)i).StartsWith(rootPath, StringComparison.InvariantCultureIgnoreCase))
                nodes.Add(new NodeWrapper(this, (uint)i, _nodes[i]));
        });

        return nodes;
    }

    public byte[] GetVolumeBitmap() => _bitmapData;

    public void Dispose()
    {
        _volumeHandle?.Dispose();
        _volumeHandle = null;
    }
}
