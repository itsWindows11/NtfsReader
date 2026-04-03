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
    /// The volume is opened with <c>FILE_FLAG_OVERLAPPED</c> on .NET 6+ so that every disk
    /// read issues a genuine kernel async I/O operation via
    /// <see cref="System.IO.RandomAccess"/>.  The file position is passed directly through
    /// the <c>OVERLAPPED</c> structure; raw volume handles do not support
    /// <c>SetFilePointer</c> and are therefore never wrapped in a
    /// <see cref="System.IO.FileStream"/>.
    /// On .NET Standard 2.0 the volume is opened synchronously and each read runs on a
    /// thread-pool thread via <see cref="Task.Run"/>, so the calling thread is never blocked.
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
    /// Asynchronously scans the MFT of <paramref name="driveInfo"/> and streams the
    /// matching nodes one by one via <c>await foreach</c>.
    /// </summary>
    /// <param name="driveInfo">The NTFS drive to read. Must not be <see langword="null"/>.</param>
    /// <param name="retrieveMode">
    /// Flags that control which optional metadata is loaded into memory.
    /// </param>
    /// <param name="rootPath">
    /// The path prefix to filter by (e.g. <c>"C:\\"</c>). Wildcards are not supported.
    /// </param>
    /// <param name="cancellationToken">A token that can cancel the scan or enumeration.</param>
    /// <returns>
    /// An <see cref="IAsyncEnumerable{INode}"/> that yields matching nodes after the
    /// async MFT scan completes.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Full path resolution requires the complete node table (parent-directory inode
    /// numbers can be higher than child inodes), so the entire MFT must be read before
    /// the first node is yielded.  The scan itself is async; nodes are then yielded
    /// sequentially without allocating an intermediate <see cref="List{INode}"/>.
    /// </para>
    /// <para>
    /// The caller must have <b>Administrator</b> privileges on Windows; otherwise an
    /// <see cref="IOException"/> is thrown.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="driveInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The volume could not be opened.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled.
    /// </exception>
    public static async IAsyncEnumerable<INode> EnumerateNodesAsync(
        DriveInfo driveInfo,
        RetrieveMode retrieveMode,
        string rootPath,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (driveInfo == null)
            throw new ArgumentNullException(nameof(driveInfo));

        // The complete node table must be built before any FullName can be resolved,
        // because a file's parent directory may have a higher inode number than the file
        // itself.  Yielding mid-scan would produce incorrect paths in those cases.
        var reader = new NtfsReader();
        await reader.InitializeAsync(driveInfo, retrieveMode, cancellationToken)
            .ConfigureAwait(false);

        int nodeCount = reader._nodes.Length;
        for (int i = 0; i < nodeCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader._nodes[i].NameIndex == 0)
                continue;

            // Path resolution requires walking the full parent chain; there is no cheaper
            // preliminary check available.  This mirrors the same call in GetNodes.
            if (!reader.GetNodeFullNameCore((uint)i)
                    .StartsWith(rootPath, StringComparison.InvariantCultureIgnoreCase))
                continue;

            yield return new NodeWrapper(reader, (uint)i, reader._nodes[i]);
        }
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
