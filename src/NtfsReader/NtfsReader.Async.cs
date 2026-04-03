using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO.Filesystem.Ntfs;

public sealed partial class NtfsReader
{
#if NET6_0_OR_GREATER
    // FILE_FLAG_OVERLAPPED enables the kernel to perform truly asynchronous I/O on the
    // volume handle.  RandomAccess.ReadAsync requires an overlapped handle on Windows
    // so that it can pass the file position via the OVERLAPPED structure rather than
    // calling SetFilePointer (which raw volume handles do not support).
    private const int FILE_FLAG_OVERLAPPED = 0x40000000;
#endif

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

#if NET6_0_OR_GREATER
        // On .NET 6+ open with FILE_FLAG_OVERLAPPED so that RandomAccess.ReadAsync issues
        // genuine kernel async I/O without blocking a thread pool thread.
        const int openFlags = FILE_FLAG_OVERLAPPED;
#else
        // On .NET Standard 2.0 open without FILE_FLAG_OVERLAPPED; reads are dispatched via
        // Task.Run so the calling thread is never blocked even though the underlying I/O
        // is synchronous.  The OVERLAPPED offset fields still specify the read position.
        const int openFlags = 0;
#endif

        _volumeHandle = CreateFile(
            volume,
            FileAccess.Read,
            FileShare.All,
            IntPtr.Zero,
            FileMode.Open,
            openFlags,
            IntPtr.Zero);

        if (_volumeHandle == null || _volumeHandle.IsInvalid)
            throw new IOException(
                $"Unable to open volume {driveInfo}. Make sure it exists and that you have Administrator privileges.");

        try
        {
            await InitializeDiskInfoAsync(cancellationToken).ConfigureAwait(false);
            _nodes = await ProcessMftAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _volumeHandle.Dispose();
            _volumeHandle = null;
        }

        _nameIndex.Clear();
        GC.Collect();
    }

    /// <summary>
    /// Issues an async read of exactly <paramref name="count"/> bytes from the volume
    /// at absolute byte offset <paramref name="absolutePosition"/> into
    /// <paramref name="buffer"/> starting at <paramref name="offset"/>.
    /// <para>
    /// On .NET 6+ this is a true kernel async operation via
    /// <see cref="System.IO.RandomAccess"/>, which passes the file position directly
    /// through the OVERLAPPED structure without calling <c>SetFilePointer</c>.
    /// On .NET Standard 2.0 the underlying synchronous <c>ReadFile</c> P/Invoke runs
    /// on a thread pool thread via <see cref="Task.Run"/>.
    /// </para>
    /// </summary>
    private async Task ReadFileAsync(
        byte[] buffer,
        int offset,
        int count,
        long absolutePosition,
        CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            cancellationToken.ThrowIfCancellationRequested();

#if NET6_0_OR_GREATER
            int read = await System.IO.RandomAccess.ReadAsync(
                _volumeHandle,
                buffer.AsMemory(offset + totalRead, count - totalRead),
                absolutePosition + totalRead,
                cancellationToken).ConfigureAwait(false);
#else
            int capturedOffset = offset + totalRead;
            int capturedCount = count - totalRead;
            long capturedPosition = absolutePosition + totalRead;
            int read = await Task.Run(
                () => ReadFileSync(buffer, capturedOffset, capturedCount, capturedPosition),
                cancellationToken).ConfigureAwait(false);
#endif

            if (read == 0)
                throw new IOException("Unable to read volume information: unexpected end of data.");

            totalRead += read;
        }
    }

#if !NET6_0_OR_GREATER
    /// <summary>
    /// Synchronous read helper used by the .NET Standard 2.0 async path.
    /// Reads <paramref name="count"/> bytes from the volume handle using the P/Invoke
    /// <c>ReadFile</c> with an OVERLAPPED offset; intended to be called from
    /// <see cref="Task.Run"/>.
    /// </summary>
    private unsafe int ReadFileSync(byte[] buffer, int offset, int count, long absolutePosition)
    {
        fixed (byte* ptr = buffer)
        {
            var overlapped = new NativeOverlapped((ulong)absolutePosition);
            if (!ReadFile(_volumeHandle, (IntPtr)(ptr + offset), (uint)count, out uint read, ref overlapped))
                throw new IOException("Unable to read volume information.");
            return (int)read;
        }
    }
#endif

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

        // Rent a buffer from the shared pool to avoid a large heap allocation.
        // ArrayPool may return a larger array; always use bufferSize for sizing, not data.Length.
        byte[] data = ArrayPool<byte>.Shared.Rent((int)bufferSize);
        try
        {
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
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }
}
