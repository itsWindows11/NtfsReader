using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("NtfsReader.Tests")]

namespace System.IO.Filesystem.Ntfs;

/// <summary>
/// Reads the Master File Table (MFT) of an NTFS volume and exposes its entries as a
/// queryable collection of <see cref="INode"/> objects.
/// </summary>
/// <remarks>
/// <para>
/// Reading the MFT is orders of magnitude faster than conventional directory enumeration
/// (e.g. <see cref="System.IO.Directory.EnumerateFiles"/>), making this library ideal for
/// volume-wide searches, disk analysis tools, and file-indexing scenarios.
/// </para>
/// <para>
/// The caller must have <b>Administrator</b> privileges; otherwise the underlying
/// volume handle cannot be opened and an <see cref="IOException"/> is thrown.
/// </para>
/// <para>
/// Use <see cref="CreateAsync"/> to avoid blocking the calling thread during the
/// (potentially multi-second) MFT scan on large volumes.
/// </para>
/// </remarks>
public sealed partial class NtfsReader
{
    /// <summary>
    /// Initializes an <see cref="NtfsReader"/> and synchronously reads the entire MFT.
    /// </summary>
    /// <param name="driveInfo">The NTFS drive to read. Must not be <see langword="null"/>.</param>
    /// <param name="retrieveMode">
    /// Flags that control which optional metadata is loaded into memory.
    /// Prefer <see cref="RetrieveMode.Minimal"/> when only names and sizes are needed, as
    /// <see cref="RetrieveMode.Streams"/> and <see cref="RetrieveMode.Fragments"/> each add
    /// significant memory overhead on volumes with millions of files.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="driveInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">
    /// The volume could not be opened — the drive may not exist, may not be NTFS, or the
    /// process lacks Administrator privileges.
    /// </exception>
    public NtfsReader(DriveInfo driveInfo, RetrieveMode retrieveMode)
    {
        _driveInfo = driveInfo ?? throw new ArgumentNullException(nameof(driveInfo));
        _retrieveMode = retrieveMode;
        _driveRootPrefix = _driveInfo.Name.TrimEnd(['\\']);

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

        using (_volumeHandle)
        {
            InitializeDiskInfo();
            _nodes = ProcessMft();
        }

        _nameIndex = null;
        _volumeHandle = null;

        GC.Collect();
    }

    /// <summary>
    /// Creates an <see cref="NtfsReader"/> asynchronously using true kernel async I/O.
    /// </summary>
    /// <param name="driveInfo">The NTFS drive to read. Must not be <see langword="null"/>.</param>
    /// <param name="retrieveMode">
    /// Flags that control which optional metadata is loaded into memory.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that can cancel the operation. Cancellation is honoured between chunk
    /// reads; an in-progress read is not interrupted mid-transfer.
    /// </param>
    /// <returns>
    /// A <see cref="Task{NtfsReader}"/> that completes when the MFT has been fully read.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The volume is opened with <c>FILE_FLAG_OVERLAPPED</c> and wrapped in a
    /// <see cref="System.IO.FileStream"/> with <c>isAsync: true</c>, so every disk read
    /// issues a genuine kernel async I/O operation (overlapped I/O). The calling thread
    /// is returned to its caller at each <c>await</c> and never blocks on disk I/O.
    /// CPU-bound MFT record parsing runs synchronously between reads.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="driveInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">
    /// The volume could not be opened — the drive may not exist, may not be NTFS, or the
    /// process lacks Administrator privileges.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled.
    /// </exception>
    public static async Task<NtfsReader> CreateAsync(
        DriveInfo driveInfo,
        RetrieveMode retrieveMode,
        CancellationToken cancellationToken = default)
    {
        if (driveInfo == null)
            throw new ArgumentNullException(nameof(driveInfo));

        var reader = new NtfsReader();
        await reader.InitializeAsync(driveInfo, retrieveMode, cancellationToken).ConfigureAwait(false);
        return reader;
    }

    /// <summary>
    /// Gets geometry and layout information for the scanned volume.
    /// </summary>
    public IDiskInfo DiskInfo => _diskInfo;

    /// <summary>
    /// Returns all nodes (files and directories) whose full path starts with
    /// <paramref name="rootPath"/>.
    /// </summary>
    /// <param name="rootPath">
    /// The path prefix to filter by — must at minimum contain the drive letter
    /// (e.g. <c>"C:\\"</c>). Subdirectories may be appended to narrow the result.
    /// Wildcards are not supported.
    /// </param>
    /// <returns>
    /// A <see cref="List{INode}"/> populated in parallel; order is non-deterministic.
    /// </returns>
    public List<INode> GetNodes(string rootPath)
    {
        var bag = new ConcurrentBag<INode>();
        int nodeCount = _nodes.Length;

        Parallel.For(0, nodeCount, i =>
        {
            if (_nodes[i].NameIndex != 0
                && GetNodeFullNameCore((uint)i).StartsWith(rootPath, StringComparison.InvariantCultureIgnoreCase))
                bag.Add(new NodeWrapper(this, (uint)i, _nodes[i]));
        });

        return new List<INode>(bag);
    }

    /// <summary>
    /// Returns the raw volume bitmap that indicates which clusters are in use.
    /// Each bit corresponds to one cluster; a set bit means the cluster is allocated.
    /// </summary>
    /// <returns>A byte array representing the volume bitmap.</returns>
    public byte[] GetVolumeBitmap() => _bitmapData;

    /// <inheritdoc/>
    public void Dispose()
    {
        _volumeHandle?.Dispose();
        _volumeHandle = null;
    }
}
