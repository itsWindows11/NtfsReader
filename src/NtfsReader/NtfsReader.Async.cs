using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO.Filesystem.Ntfs;

public sealed partial class NtfsReader
{
    // FILE_FLAG_OVERLAPPED tells the kernel to perform truly asynchronous I/O on the
    // volume handle.  Without this flag, ReadFile always blocks the calling thread even
    // when invoked through FileStream.ReadAsync.
    private const int FILE_FLAG_OVERLAPPED = 0x40000000;

    // Holds the async-capable FileStream only while InitializeAsync is executing.
    // Null at all other times.
    private System.IO.FileStream _asyncStream;

    /// <summary>
    /// Drives the async initialization path. Called exclusively by
    /// <see cref="CreateAsync"/>.
    /// </summary>
    internal async Task InitializeAsync(
        DriveInfo driveInfo,
        RetrieveMode retrieveMode,
        CancellationToken cancellationToken)
    {
        _driveInfo = driveInfo;
        _retrieveMode = retrieveMode;
        _driveRootPrefix = _driveInfo.Name.TrimEnd(['\\']);

        var builder = new StringBuilder(1024);
        GetVolumeNameForVolumeMountPoint(_driveInfo.RootDirectory.Name, builder, builder.Capacity);
        string volume = builder.ToString().TrimEnd(['\\']);

        // Open the volume with FILE_FLAG_OVERLAPPED so the OS performs true async I/O.
        _volumeHandle = CreateFile(
            volume,
            FileAccess.Read,
            FileShare.All,
            IntPtr.Zero,
            FileMode.Open,
            FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (_volumeHandle == null || _volumeHandle.IsInvalid)
            throw new IOException(
                $"Unable to open volume {driveInfo}. Make sure it exists and that you have Administrator privileges.");

        // Wrap the overlapped handle in a FileStream with isAsync:true.
        // The FileStream takes ownership of the SafeFileHandle (closes it on Dispose).
        _asyncStream = new System.IO.FileStream(
            _volumeHandle,
            System.IO.FileAccess.Read,
            bufferSize: 4096,
            isAsync: true);
        _volumeHandle = null; // owned by _asyncStream from here on

        try
        {
            await InitializeDiskInfoAsync(cancellationToken).ConfigureAwait(false);
            _nodes = await ProcessMftAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _asyncStream.Dispose();
            _asyncStream = null;
        }

        _nameIndex.Clear();
        GC.Collect();
    }
    /// <summary>
    /// Issues a true async read of exactly <paramref name="count"/> bytes from the volume
    /// at absolute byte offset <paramref name="absolutePosition"/> into
    /// <paramref name="buffer"/> starting at <paramref name="offset"/>.
    /// </summary>
    private async Task ReadFileAsync(
        byte[] buffer,
        int offset,
        int count,
        long absolutePosition,
        CancellationToken cancellationToken)
    {
        // FileStream tracks its own position independently of the OS file pointer when
        // opened with isAsync:true (overlapped I/O).  Seek updates that internal position;
        // ReadAsync then passes it via the OVERLAPPED structure to the kernel.
        _asyncStream.Seek(absolutePosition, System.IO.SeekOrigin.Begin);

        int totalRead = 0;
        while (totalRead < count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int read = await _asyncStream
                .ReadAsync(buffer, offset + totalRead, count - totalRead, cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
                throw new IOException("Unable to read volume information: unexpected end of data.");

            totalRead += read;
        }
    }

    private async Task InitializeDiskInfoAsync(CancellationToken cancellationToken)
    {
        byte[] volumeData = new byte[512];
        await ReadFileAsync(volumeData, 0, volumeData.Length, 0L, cancellationToken)
            .ConfigureAwait(false);

        // ParseBootSectorData uses `fixed` internally; safe to call from an async method
        // because the fixed block does not span any await point.
        ParseBootSectorData(volumeData);
    }

    private async Task<byte[]> ProcessBitmapDataAsync(
        List<Stream> mftStreams,
        CancellationToken cancellationToken)
    {
        ulong vcn = 0;
        ulong maxMftBitmapBytes = 0;

        Stream bitmapStream = SearchStream(mftStreams, AttributeType.AttributeBitmap)
            ?? throw new Exception("No Bitmap Data");

        foreach (Fragment fragment in bitmapStream.Fragments)
        {
            if (fragment.Lcn != VIRTUALFRAGMENT)
                maxMftBitmapBytes += (fragment.NextVcn - vcn) * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster;

            vcn = fragment.NextVcn;
        }

        byte[] bitmapData = new byte[maxMftBitmapBytes];

        vcn = 0;
        ulong realVcn = 0;

        foreach (Fragment fragment in bitmapStream.Fragments)
        {
            if (fragment.Lcn != VIRTUALFRAGMENT)
            {
                long position = (long)(fragment.Lcn * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster);
                int length = (int)((fragment.NextVcn - vcn) * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster);
                int bufOffset = (int)(realVcn * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster);

                await ReadFileAsync(bitmapData, bufOffset, length, position, cancellationToken)
                    .ConfigureAwait(false);

                realVcn += fragment.NextVcn - vcn;
            }

            vcn = fragment.NextVcn;
        }

        return bitmapData;
    }

    /// <summary>
    /// Async mirror of <see cref="ProcessMft"/>. Each disk read is a true kernel async
    /// operation (via <see cref="System.IO.FileStream.ReadAsync"/>); CPU-bound MFT record
    /// parsing runs synchronously between reads and never crosses an await point while
    /// holding a pinned pointer.
    /// </summary>
    private async Task<Node[]> ProcessMftAsync(CancellationToken cancellationToken)
    {
        uint bufferSize = (Environment.OSVersion.Version.Major >= 6 ? 256u : 64u) * 1024;
        byte[] data = new byte[bufferSize];

        // --- Read and process the $MFT inode (always inode 0). ---
        long mftStartPos = (long)(_diskInfo.MftStartLcn * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster);

        await ReadFileAsync(data, 0, (int)_diskInfo.BytesPerMftRecord, mftStartPos, cancellationToken)
            .ConfigureAwait(false);

        // FixupRawMftdataAt and ProcessMftRecordAt both pin `data` with `fixed` internally;
        // no await crosses a fixed block, so the GC is free to move `data` between calls.
        FixupRawMftdataAt(data, 0, _diskInfo.BytesPerMftRecord);

        var mftStreams = new List<Stream>();

        if ((_retrieveMode & RetrieveMode.StandardInformations) == RetrieveMode.StandardInformations)
            _standardInformations = new StandardInformation[1];

        if (!ProcessMftRecordAt(data, 0, _diskInfo.BytesPerMftRecord, 0, out Node mftNode, mftStreams, true))
            throw new Exception("Can't interpret MFT Record");

        // Handle both non-resident (common) and resident (small/new volumes) $MFT bitmap.
        // On small volumes the $BITMAP attribute can be resident; there is no run-list so
        // ProcessBitmapDataAsync would throw "No Bitmap Data".  Fall back to extracting the
        // bytes directly from the managed byte[] that is still in `data`.
        _bitmapData = SearchStream(mftStreams, AttributeType.AttributeBitmap) != null
            ? await ProcessBitmapDataAsync(mftStreams, cancellationToken).ConfigureAwait(false)
            : TryExtractResidentBitmapDataAt(data, _diskInfo.BytesPerMftRecord)
              ?? throw new Exception("No Bitmap Data");

        OnBitmapDataAvailable();

        Stream dataStream = SearchStream(mftStreams, AttributeType.AttributeData);

        uint maxInode = (uint)_bitmapData.Length * 8;
        if (maxInode > (uint)(dataStream.Size / _diskInfo.BytesPerMftRecord))
            maxInode = (uint)(dataStream.Size / _diskInfo.BytesPerMftRecord);

        Node[] nodes = new Node[maxInode];
        nodes[0] = mftNode;

        if ((_retrieveMode & RetrieveMode.StandardInformations) == RetrieveMode.StandardInformations)
        {
            StandardInformation mftInfo = _standardInformations[0];
            _standardInformations = new StandardInformation[maxInode];
            _standardInformations[0] = mftInfo;
        }

        if ((_retrieveMode & RetrieveMode.Streams) == RetrieveMode.Streams)
            _streams = new Stream[maxInode][];

        // --- Main MFT scan loop ---
        // State mirrors ReadNextChunk's ref parameters in the sync path.
        ulong blockStart = 0, blockEnd = 0, realVcn = 0, vcn = 0;
        int fragmentIndex = 0;
        int fragmentCount = dataStream.Fragments.Count;

        for (uint nodeIndex = 1; nodeIndex < maxInode; nodeIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip inodes the bitmap says are free.
            if ((_bitmapData[nodeIndex >> 3] & BitmapMasks[nodeIndex % 8]) == 0)
                continue;

            if (nodeIndex >= blockEnd)
            {
                // --- Compute which chunk to read next (mirrors ReadNextChunk's sync logic). ---
                blockStart = nodeIndex;
                blockEnd = blockStart + bufferSize / _diskInfo.BytesPerMftRecord;
                if (blockEnd > dataStream.Size * 8)
                    blockEnd = dataStream.Size * 8;

                ulong u1 = 0;

                while (fragmentIndex < fragmentCount)
                {
                    Fragment frag = dataStream.Fragments[fragmentIndex];
                    u1 = (realVcn + frag.NextVcn - vcn)
                         * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster
                         / _diskInfo.BytesPerMftRecord;

                    if (u1 > nodeIndex)
                        break;

                    do
                    {
                        if (frag.Lcn != VIRTUALFRAGMENT)
                            realVcn += frag.NextVcn - vcn;

                        vcn = frag.NextVcn;

                        if (++fragmentIndex >= fragmentCount)
                            break;

                    } while (frag.Lcn == VIRTUALFRAGMENT);

                    if (fragmentIndex >= fragmentCount)
                        break;
                }

                if (fragmentIndex >= fragmentCount)
                    break;

                if (blockEnd >= u1)
                    blockEnd = u1;

                ulong position =
                    (dataStream.Fragments[fragmentIndex].Lcn - realVcn)
                    * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster
                    + blockStart * _diskInfo.BytesPerMftRecord;

                ulong readLen = (blockEnd - blockStart) * _diskInfo.BytesPerMftRecord;

                // True async kernel I/O read.
                await ReadFileAsync(data, 0, (int)readLen, (long)position, cancellationToken)
                    .ConfigureAwait(false);
            }

            ulong recordOffset = (nodeIndex - blockStart) * _diskInfo.BytesPerMftRecord;

            FixupRawMftdataAt(data, recordOffset, _diskInfo.BytesPerMftRecord);

            List<Stream> streams = null;
            if ((_retrieveMode & RetrieveMode.Streams) == RetrieveMode.Streams)
                streams = [];

            if (!ProcessMftRecordAt(data, recordOffset, _diskInfo.BytesPerMftRecord, nodeIndex, out Node newNode, streams, false))
                continue;

            nodes[nodeIndex] = newNode;

            if (streams != null)
                _streams[nodeIndex] = [.. streams];
        }

        return nodes;
    }
}
