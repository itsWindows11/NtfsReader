using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32.SafeHandles;

namespace System.IO.Filesystem.Ntfs;

public sealed partial class NtfsReader : IDisposable
{
    private const ulong VIRTUALFRAGMENT = ulong.MaxValue;
    private const uint ROOTDIRECTORY = 5;

    private readonly byte[] BitmapMasks = [1, 2, 4, 8, 16, 32, 64, 128];

    internal SafeFileHandle _volumeHandle;
    internal DiskInfoWrapper _diskInfo;
    internal Node[] _nodes;
    internal StandardInformation[] _standardInformations;
    internal Stream[][] _streams;
    internal DriveInfo _driveInfo;
    internal List<string> _names = [];
    internal RetrieveMode _retrieveMode;
    internal byte[] _bitmapData;

    //preallocate a lot of space for the strings to avoid too much dictionary resizing
    //use ordinal comparison to improve performance
    //this will be deallocated once the MFT reading is finished
    private readonly Dictionary<string, int> _nameIndex = new(128 * 1024, StringComparer.Ordinal);

    /// <summary>
    /// Raised once the bitmap data has been read.
    /// </summary>
    public event EventHandler BitmapDataAvailable;

    private void OnBitmapDataAvailable()
        => BitmapDataAvailable?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Allocate or retrieve an existing index for the particular string.
    /// </summary>
    ///<remarks>
    /// In order to mimize memory usage, we reuse string as much as possible.
    ///</remarks>
    private int GetNameIndex(string name)
    {
        if (_nameIndex.TryGetValue(name, out int existingIndex))
            return existingIndex;

        _names.Add(name);
        _nameIndex[name] = _names.Count - 1;

        return _names.Count - 1;
    }

    /// <summary>
    /// Get the string from our stringtable from the given index.
    /// </summary>
    internal string GetNameFromIndex(int nameIndex)
        => nameIndex == 0 ? null : _names[nameIndex];

    internal Stream SearchStream(List<Stream> streams, AttributeType streamType)
        => streams.FirstOrDefault(s => s.Type == streamType);

    internal Stream SearchStream(List<Stream> streams, AttributeType streamType, int streamNameIndex)
        => streams.FirstOrDefault(s => s.Type == streamType && s.NameIndex == streamNameIndex);

    private unsafe void ReadFile(byte* buffer, int len, ulong absolutePosition) => ReadFile(buffer, (ulong)len, absolutePosition);

    private unsafe void ReadFile(byte* buffer, uint len, ulong absolutePosition) => ReadFile(buffer, (ulong)len, absolutePosition);

    private unsafe void ReadFile(byte* buffer, ulong len, ulong absolutePosition)
    {
        var overlapped = new NativeOverlapped(absolutePosition);

        if (!ReadFile(_volumeHandle, (IntPtr)buffer, (uint)len, out uint read, ref overlapped))
            throw new Exception("Unable to read volume information");

        if (read != (uint)len)
            throw new Exception("Unable to read volume information");
    }

    /// <summary>
    /// Read the next contiguous block of information on disk
    /// </summary>
    private unsafe bool ReadNextChunk(
        byte* buffer,
        uint bufferSize,
        uint nodeIndex,
        int fragmentIndex,
        Stream dataStream,
        ref ulong BlockStart,
        ref ulong BlockEnd,
        ref ulong Vcn,
        ref ulong RealVcn
    )
    {
        BlockStart = nodeIndex;
        BlockEnd = BlockStart + bufferSize / _diskInfo.BytesPerMftRecord;
        if (BlockEnd > dataStream.Size * 8)
            BlockEnd = dataStream.Size * 8;

        ulong u1 = 0;

        int fragmentCount = dataStream.Fragments.Count;
        while (fragmentIndex < fragmentCount)
        {
            Fragment fragment = dataStream.Fragments[fragmentIndex];

            /* Calculate Inode at the end of the fragment. */
            u1 = (RealVcn + fragment.NextVcn - Vcn) * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster / _diskInfo.BytesPerMftRecord;

            if (u1 > nodeIndex)
                break;

            do
            {
                if (fragment.Lcn != VIRTUALFRAGMENT)
                    RealVcn = RealVcn + fragment.NextVcn - Vcn;

                Vcn = fragment.NextVcn;

                if (++fragmentIndex >= fragmentCount)
                    break;

            } while (fragment.Lcn == VIRTUALFRAGMENT);
        }

        if (fragmentIndex >= fragmentCount)
            return false;

        if (BlockEnd >= u1)
            BlockEnd = u1;

        ulong position =
            (dataStream.Fragments[fragmentIndex].Lcn - RealVcn) * _diskInfo.BytesPerSector *
                _diskInfo.SectorsPerCluster + BlockStart * _diskInfo.BytesPerMftRecord;

        ReadFile(buffer, (BlockEnd - BlockStart) * _diskInfo.BytesPerMftRecord, position);

        return true;
    }

    /// <summary>
    /// Gather basic disk information we need to interpret data
    /// </summary>
    private unsafe void InitializeDiskInfo()
    {
        byte[] volumeData = new byte[512];

        fixed (byte* ptr = volumeData)
        {
            ReadFile(ptr, volumeData.Length, 0);

            BootSector* bootSector = (BootSector*)ptr;

            if (bootSector->Signature != 0x202020205346544E)
                throw new Exception("The requested disk does not use the NTFS filesystem. You may want to do file enumeration using other methods like System.IO.");

            var diskInfo = new DiskInfoWrapper
            {
                BytesPerSector = bootSector->BytesPerSector,
                SectorsPerCluster = bootSector->SectorsPerCluster,
                TotalSectors = bootSector->TotalSectors,
                MftStartLcn = bootSector->MftStartLcn,
                Mft2StartLcn = bootSector->Mft2StartLcn,
                ClustersPerMftRecord = bootSector->ClustersPerMftRecord,
                ClustersPerIndexRecord = bootSector->ClustersPerIndexRecord
            };

            if (bootSector->ClustersPerMftRecord >= 128)
                diskInfo.BytesPerMftRecord = ((ulong)1 << (byte)(256 - (byte)bootSector->ClustersPerMftRecord));
            else
                diskInfo.BytesPerMftRecord = diskInfo.ClustersPerMftRecord * diskInfo.BytesPerSector * diskInfo.SectorsPerCluster;

            diskInfo.BytesPerCluster = (ulong)(diskInfo.BytesPerSector * diskInfo.SectorsPerCluster);

            if (diskInfo.SectorsPerCluster > 0)
                diskInfo.TotalClusters = diskInfo.TotalSectors / diskInfo.SectorsPerCluster;

            _diskInfo = diskInfo;
        }
    }

    /// <summary>
    /// Used to check/adjust data before we begin to interpret it
    /// </summary>
    private unsafe void FixupRawMftdata(byte* buffer, ulong len)
    {
        FileRecordHeader* ntfsFileRecordHeader = (FileRecordHeader*)buffer;

        if (ntfsFileRecordHeader->RecordHeader.Type != RecordType.File)
            return;

        ushort* wordBuffer = (ushort*)buffer;

        ushort* UpdateSequenceArray = (ushort*)(buffer + ntfsFileRecordHeader->RecordHeader.UsaOffset);
        uint increment = (uint)_diskInfo.BytesPerSector / sizeof(ushort);

        uint Index = increment - 1;

        for (int i = 1; i < ntfsFileRecordHeader->RecordHeader.UsaCount; i++)
        {
            /* Check if we are inside the buffer. */
            if (Index * sizeof(ushort) >= len)
                throw new Exception("USA data indicates that data is missing, the MFT may be corrupt.");

            // Check if the last 2 bytes of the sector contain the Update Sequence Number.
            if (wordBuffer[Index] != UpdateSequenceArray[0])
                throw new Exception("USA fixup word is not equal to the Update Sequence Number, the MFT may be corrupt.");

            /* Replace the last 2 bytes in the sector with the value from the Usa array. */
            wordBuffer[Index] = UpdateSequenceArray[i];
            Index += increment;
        }
    }

    /// <summary>
    /// Decode the RunLength value.
    /// </summary>
    private static unsafe long ProcessRunLength(byte* runData, uint runDataLength, int runLengthSize, ref uint index)
    {
        long runLength = 0;
        byte* runLengthBytes = (byte*)&runLength;

        for (int i = 0; i < runLengthSize; i++)
        {
            runLengthBytes[i] = runData[index];

            if (++index >= runDataLength)
                throw new Exception("Datarun is longer than buffer, the MFT may be corrupt.");
        }

        return runLength;
    }

    /// <summary>
    /// Decode the RunOffset value.
    /// </summary>
    private static unsafe long ProcessRunOffset(byte* runData, uint runDataLength, int runOffsetSize, ref uint index)
    {
        long runOffset = 0;
        byte* runOffsetBytes = (byte*)&runOffset;

        int i;

        for (i = 0; i < runOffsetSize; i++)
        {
            runOffsetBytes[i] = runData[index];
            if (++index >= runDataLength)
                throw new Exception("Datarun is longer than buffer, the MFT may be corrupt.");
        }

        // Process negative values
        if (runOffsetBytes[i - 1] >= 0x80)
            while (i < 8)
                runOffsetBytes[i++] = 0xFF;

        return runOffset;
    }

    /// <summary>
    /// Read the data that is specified in a RunData list from disk into memory,
    /// skipping the first Offset bytes.
    /// </summary>
    private unsafe byte[] ProcessNonResidentData(
        byte* RunData,
        uint RunDataLength,
        ulong Offset,
        ulong WantedLength
    )
    {
        if (RunData == null || RunDataLength == 0)
            throw new Exception("Nothing to read in the RunData.");

        if (WantedLength >= uint.MaxValue)
            throw new Exception("Too many bytes to read in the RunData.");

        /* We have to round up the WantedLength to the nearest sector. For some
           reason or other Microsoft has decided that raw reading from disk can
           only be done by whole sector, even though ReadFile() accepts it's
           parameters in bytes. */
        if (WantedLength % _diskInfo.BytesPerSector > 0)
            WantedLength += _diskInfo.BytesPerSector - (WantedLength % _diskInfo.BytesPerSector);

        /* Walk through the RunData and read the requested data from disk. */
        uint Index = 0;
        long Lcn = 0;
        long Vcn = 0;

        byte[] buffer = new byte[WantedLength];

        fixed (byte* bufPtr = buffer)
        {
            while (RunData[Index] != 0)
            {
                /* Decode the RunData and calculate the next Lcn. */
                int RunLengthSize = (RunData[Index] & 0x0F);
                int RunOffsetSize = ((RunData[Index] & 0xF0) >> 4);
                
                if (++Index >= RunDataLength)
                    throw new Exception("Error: datarun is longer than buffer, the MFT may be corrupt.");

                long RunLength =
                    ProcessRunLength(RunData, RunDataLength, RunLengthSize, ref Index);

                long RunOffset =
                    ProcessRunOffset(RunData, RunDataLength, RunOffsetSize, ref Index);

                // Ignore virtual extents.
                if (RunOffset == 0 || RunLength == 0)
                    continue;

                Lcn += RunOffset;
                Vcn += RunLength;

                /* Determine how many and which bytes we want to read. If we don't need
                   any bytes from this extent then loop. */
                ulong ExtentVcn = (ulong)((Vcn - RunLength) * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster);
                ulong ExtentLcn = (ulong)(Lcn * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster);
                ulong ExtentLength = (ulong)(RunLength * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster);

                if (Offset >= ExtentVcn + ExtentLength)
                    continue;

                if (Offset > ExtentVcn)
                {
                    ExtentLcn = ExtentLcn + Offset - ExtentVcn;
                    ExtentLength -= (Offset - ExtentVcn);
                    ExtentVcn = Offset;
                }

                if (Offset + WantedLength <= ExtentVcn)
                    continue;

                if (Offset + WantedLength < ExtentVcn + ExtentLength)
                    ExtentLength = Offset + WantedLength - ExtentVcn;

                if (ExtentLength == 0)
                    continue;

                ReadFile(bufPtr + ExtentVcn - Offset, ExtentLength, ExtentLcn);
            }
        }

        return buffer;
    }

    /// <summary>
    /// Process each attributes and gather information when necessary
    /// </summary>
    private unsafe void ProcessAttributes(ref Node node, uint nodeIndex, byte* ptr, ulong BufLength, ushort instance, int depth, List<Stream> streams, bool isMftNode)
    {
        Attribute* attribute = null;
        for (uint AttributeOffset = 0; AttributeOffset < BufLength; AttributeOffset += attribute->Length)
        {
            attribute = (Attribute*)(ptr + AttributeOffset);

            // exit the loop if end-marker.
            if ((AttributeOffset + 4 <= BufLength) &&
                (*(uint*)attribute == 0xFFFFFFFF))
                break;

            //make sure we did read the data correctly
            if ((AttributeOffset + 4 > BufLength) || attribute->Length < 3 ||
                (AttributeOffset + attribute->Length > BufLength))
                throw new Exception("Error: attribute in Inode %I64u is bigger than the data, the MFT may be corrupt.");

            //attributes list needs to be processed at the end
            if (attribute->AttributeType == AttributeType.AttributeAttributeList)
                continue;

            /* If the Instance does not equal the AttributeNumber then ignore the attribute.
               This is used when an AttributeList is being processed and we only want a specific
               instance. */
            if ((instance != 65535) && (instance != attribute->AttributeNumber))
                continue;

            if (attribute->Nonresident == 0)
            {
                ResidentAttribute* residentAttribute = (ResidentAttribute*)attribute;

                switch (attribute->AttributeType)
                {
                    case AttributeType.AttributeFileName:
                        AttributeFileName* attributeFileName = (AttributeFileName*)(ptr + AttributeOffset + residentAttribute->ValueOffset);

                        if (attributeFileName->ParentDirectory.InodeNumberHighPart > 0)
                            throw new NotSupportedException("48 bits inode are not supported to reduce memory footprint.");

                        //node.ParentNodeIndex = ((ulong)attributeFileName->ParentDirectory.InodeNumberHighPart << 32) + attributeFileName->ParentDirectory.InodeNumberLowPart;
                        node.ParentNodeIndex = attributeFileName->ParentDirectory.InodeNumberLowPart;

                        if (attributeFileName->NameType == 1 || node.NameIndex == 0)
                            node.NameIndex = GetNameIndex(new string(&attributeFileName->Name, 0, attributeFileName->NameLength));

                        break;

                    case AttributeType.AttributeStandardInformation:
                        AttributeStandardInformation* attributeStandardInformation = (AttributeStandardInformation*)(ptr + AttributeOffset + residentAttribute->ValueOffset);

                        node.Attributes |= (Attributes)attributeStandardInformation->FileAttributes;

                        if ((_retrieveMode & RetrieveMode.StandardInformations) == RetrieveMode.StandardInformations)
                            _standardInformations[nodeIndex] =
                                new StandardInformation(
                                    attributeStandardInformation->CreationTime,
                                    attributeStandardInformation->FileChangeTime,
                                    attributeStandardInformation->LastAccessTime
                                );

                        break;

                    case AttributeType.AttributeData:
                        node.Size = residentAttribute->ValueLength;
                        break;
                }
            }
            else
            {
                NonResidentAttribute* nonResidentAttribute = (NonResidentAttribute*)attribute;

                //save the length (number of bytes) of the data.
                if (attribute->AttributeType == AttributeType.AttributeData && node.Size == 0)
                    node.Size = nonResidentAttribute->DataSize;

                if (streams != null)
                {
                    //extract the stream name
                    int streamNameIndex = 0;
                    if (attribute->NameLength > 0)
                        streamNameIndex = GetNameIndex(new string((char*)(ptr + AttributeOffset + attribute->NameOffset), 0, (int)attribute->NameLength));

                    //find or create the stream
                    Stream stream = 
                        SearchStream(streams, attribute->AttributeType, streamNameIndex);

                    if (stream == null)
                    {
                        stream = new Stream(streamNameIndex, attribute->AttributeType, nonResidentAttribute->DataSize);
                        streams.Add(stream);
                    }
                    else if (stream.Size == 0)
                        stream.Size = nonResidentAttribute->DataSize;

                    //we need the fragment of the MFTNode so retrieve them this time
                    //even if fragments aren't normally read
                    if (isMftNode || (_retrieveMode & RetrieveMode.Fragments) == RetrieveMode.Fragments)
                        ProcessFragments(
                            ref node,
                            stream,
                            ptr + AttributeOffset + nonResidentAttribute->RunArrayOffset,
                            attribute->Length - nonResidentAttribute->RunArrayOffset,
                            nonResidentAttribute->StartingVcn
                        );
                }
            }
        }

        //for (uint AttributeOffset = 0; AttributeOffset < BufLength; AttributeOffset = AttributeOffset + attribute->Length)
        //{
        //    attribute = (Attribute*)&ptr[AttributeOffset];

        //    if (*(uint*)attribute == 0xFFFFFFFF)
        //        break;

        //    if (attribute->AttributeType != AttributeType.AttributeAttributeList)
        //        continue;

        //    if (attribute->Nonresident == 0)
        //    {
        //        ResidentAttribute* residentAttribute = (ResidentAttribute*)attribute;

        //        ProcessAttributeList(
        //            node,
        //            ptr + AttributeOffset + residentAttribute->ValueOffset,
        //            residentAttribute->ValueLength,
        //            depth
        //            );
        //    }
        //    else
        //    {
        //        NonResidentAttribute* nonResidentAttribute = (NonResidentAttribute*)attribute;

        //        byte[] buffer =
        //            ProcessNonResidentData(
        //                ptr + AttributeOffset + nonResidentAttribute->RunArrayOffset,
        //                attribute->Length - nonResidentAttribute->RunArrayOffset,
        //                0,
        //                nonResidentAttribute->DataSize
        //          );

        //        fixed (byte* bufPtr = buffer)
        //            ProcessAttributeList(node, bufPtr, nonResidentAttribute->DataSize, depth + 1);
        //    }
        //}

        if (streams != null && streams.Count > 0)
            node.Size = streams[0].Size;
    }

    //private unsafe void ProcessAttributeList(Node mftNode, Node node, byte* ptr, ulong bufLength, int depth, InterpretMode interpretMode)
    //{
    //    if (ptr == null || bufLength == 0)
    //        return;

    //    if (depth > 1000)
    //        throw new Exception("Error: infinite attribute loop, the MFT may be corrupt.");

    //    AttributeList* attribute = null;
    //    for (uint AttributeOffset = 0; AttributeOffset < bufLength; AttributeOffset = AttributeOffset + attribute->Length)
    //    {
    //        attribute = (AttributeList*)&ptr[AttributeOffset];

    //        /* Exit if no more attributes. AttributeLists are usually not closed by the
    //           0xFFFFFFFF endmarker. Reaching the end of the buffer is therefore normal and
    //           not an error. */
    //        if (AttributeOffset + 3 > bufLength) break;
    //        if (*(uint*)attribute == 0xFFFFFFFF) break;
    //        if (attribute->Length < 3) break;
    //        if (AttributeOffset + attribute->Length > bufLength) break;

    //        /* Extract the referenced Inode. If it's the same as the calling Inode then ignore
    //           (if we don't ignore then the program will loop forever, because for some
    //           reason the info in the calling Inode is duplicated here...). */
    //        ulong RefInode = ((ulong)attribute->FileReferenceNumber.InodeNumberHighPart << 32) + attribute->FileReferenceNumber.InodeNumberLowPart;
    //        if (RefInode == node.NodeIndex)
    //            continue;

    //        /* Extract the streamname. I don't know why AttributeLists can have names, and
    //           the name is not used further down. It is only extracted for debugging purposes.
    //           */
    //        string streamName;
    //        if (attribute->NameLength > 0)
    //            streamName = new string((char*)((ulong)ptr + AttributeOffset + attribute->NameOffset), 0, attribute->NameLength);

    //        /* Find the fragment in the MFT that contains the referenced Inode. */
    //        ulong Vcn = 0;
    //        ulong RealVcn = 0;
    //        ulong RefInodeVcn = (RefInode * _diskInfo.BytesPerMftRecord) / (ulong)(_diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster);

    //        Stream dataStream = null;
    //        foreach (Stream stream in mftNode.Streams)
    //            if (stream.Type == AttributeType.AttributeData)
    //            {
    //                dataStream = stream;
    //                break;
    //            }

    //        Fragment? fragment = null;
    //        for (int i = 0; i < dataStream.Fragments.Count; ++i)
    //        {
    //            fragment = dataStream.Fragments[i];

    //            if (fragment.Value.Lcn != VIRTUALFRAGMENT)
    //            {
    //                if ((RefInodeVcn >= RealVcn) && (RefInodeVcn < RealVcn + fragment.Value.NextVcn - Vcn))
    //                    break;

    //                RealVcn = RealVcn + fragment.Value.NextVcn - Vcn;
    //            }

    //            Vcn = fragment.Value.NextVcn;
    //        }

    //        if (fragment == null)
    //            throw new Exception("Error: Inode %I64u is an extension of Inode %I64u, but does not exist (outside the MFT).");

    //        /* Fetch the record of the referenced Inode from disk. */
    //        byte[] buffer = new byte[_diskInfo.BytesPerMftRecord];

    //        NativeOverlapped overlapped =
    //            new NativeOverlapped(
    //                fragment.Value.Lcn - RealVcn * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster + RefInode * _diskInfo.BytesPerMftRecord
    //                );

    //        fixed (byte* bufPtr = buffer)
    //        {
    //            uint read;
    //            bool result =
    //                ReadFile(
    //                    _volumeHandle,
    //                    (IntPtr)bufPtr,
    //                    (uint)_diskInfo.BytesPerMftRecord,
    //                    out read,
    //                    ref overlapped
    //                    );

    //            if (!result)
    //                throw new Exception("error reading disk");

    //            /* Fixup the raw data. */
    //            FixupRawMftdata(bufPtr, _diskInfo.BytesPerMftRecord);

    //            /* If the Inode is not in use then skip. */
    //            FileRecordHeader* fileRecordHeader = (FileRecordHeader*)bufPtr;
    //            if ((fileRecordHeader->Flags & 1) != 1)
    //                continue;

    //            ///* If the BaseInode inside the Inode is not the same as the calling Inode then
    //            //   skip. */
    //            ulong baseInode = ((ulong)fileRecordHeader->BaseFileRecord.InodeNumberHighPart << 32) + fileRecordHeader->BaseFileRecord.InodeNumberLowPart;
    //            if (node.NodeIndex != baseInode)
    //                continue;

    //            ///* Process the list of attributes in the Inode, by recursively calling the
    //            //   ProcessAttributes() subroutine. */
    //            ProcessAttributes(
    //                node,
    //                bufPtr + fileRecordHeader->AttributeOffset,
    //                _diskInfo.BytesPerMftRecord - fileRecordHeader->AttributeOffset,
    //                attribute->Instance,
    //                depth + 1
    //                );
    //        }
    //    }
    //}

    /// <summary>
    /// Process fragments for streams
    /// </summary>
    private unsafe void ProcessFragments(
        ref Node node,
        Stream stream,
        byte* runData,
        uint runDataLength,
        ulong StartingVcn)
    {
        if (runData == null)
            return;

        /* Walk through the RunData and add the extents. */
        uint index = 0;
        long lcn = 0;
        long vcn = (long)StartingVcn;
        int runOffsetSize = 0;
        int runLengthSize = 0;

        while (runData[index] != 0)
        {
            /* Decode the RunData and calculate the next Lcn. */
            runLengthSize = (runData[index] & 0x0F);
            runOffsetSize = ((runData[index] & 0xF0) >> 4);

            if (++index >= runDataLength)
                throw new InvalidOperationException("Error: datarun is longer than buffer, the MFT may be corrupt.");

            long runLength = 
                ProcessRunLength(runData, runDataLength, runLengthSize, ref index);

            long runOffset =
                ProcessRunOffset(runData, runDataLength, runOffsetSize, ref index);
         
            lcn += runOffset;
            vcn += runLength;

            /* Add the size of the fragment to the total number of clusters.
               There are two kinds of fragments: real and virtual. The latter do not
               occupy clusters on disk, but are information used by compressed
               and sparse files. */
            if (runOffset != 0)
                stream.Clusters += (ulong)runLength;

            stream.Fragments.Add(
                new Fragment(
                    runOffset == 0 ? VIRTUALFRAGMENT : (ulong)lcn,
                    (ulong)vcn
                )
            );
        }
    }
    
    /// <summary>
    /// Process an actual MFT record from the buffer
    /// </summary>
    private unsafe bool ProcessMftRecord(byte* buffer, ulong length, uint nodeIndex, out Node node, List<Stream> streams, bool isMftNode)
    {
        node = new Node();

        FileRecordHeader* ntfsFileRecordHeader = (FileRecordHeader*)buffer;

        if (ntfsFileRecordHeader->RecordHeader.Type != RecordType.File)
            return false;

        //the inode is not in use
        if ((ntfsFileRecordHeader->Flags & 1) != 1)
            return false;

        ulong baseInode = ((ulong)ntfsFileRecordHeader->BaseFileRecord.InodeNumberHighPart << 32) + ntfsFileRecordHeader->BaseFileRecord.InodeNumberLowPart;

        //This is an inode extension used in an AttributeAttributeList of another inode, don't parse it
        if (baseInode != 0)
            return false;

        if (ntfsFileRecordHeader->AttributeOffset >= length)
            throw new Exception("Error: attributes in Inode %I64u are outside the FILE record, the MFT may be corrupt.");

        if (ntfsFileRecordHeader->BytesInUse > length)
            throw new Exception("Error: in Inode %I64u the record is bigger than the size of the buffer, the MFT may be corrupt.");

        //make the file appear in the rootdirectory by default
        node.ParentNodeIndex = ROOTDIRECTORY;
        
        if ((ntfsFileRecordHeader->Flags & 2) == 2)
            node.Attributes |= Attributes.Directory;

        ProcessAttributes(ref node, nodeIndex, buffer + ntfsFileRecordHeader->AttributeOffset, length - ntfsFileRecordHeader->AttributeOffset, 65535, 0, streams, isMftNode);

        return true;
    }

    /// <summary>
    /// Process the bitmap data that contains information on inode usage.
    /// </summary>
    private unsafe byte[] ProcessBitmapData(List<Stream> streams)
    {
        ulong Vcn = 0;
        ulong MaxMftBitmapBytes = 0;

        Stream bitmapStream = SearchStream(streams, AttributeType.AttributeBitmap) ?? throw new Exception("No Bitmap Data");
        foreach (Fragment fragment in bitmapStream.Fragments)
        {
            if (fragment.Lcn != VIRTUALFRAGMENT)
                MaxMftBitmapBytes += (fragment.NextVcn - Vcn) * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster;

            Vcn = fragment.NextVcn;
        }

        byte[] bitmapData = new byte[MaxMftBitmapBytes];

        fixed (byte* bitmapDataPtr = bitmapData)
        {
            Vcn = 0;
            ulong RealVcn = 0;

            foreach (Fragment fragment in bitmapStream.Fragments)
            {
                if (fragment.Lcn != VIRTUALFRAGMENT)
                {
                    ReadFile(
                        bitmapDataPtr + RealVcn * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster,
                        (fragment.NextVcn - Vcn) * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster,
                        fragment.Lcn * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster
                        );

                    RealVcn = RealVcn + fragment.NextVcn - Vcn;
                }

                Vcn = fragment.NextVcn;
            }
        }

        return bitmapData;
    }

    /// <summary>
    /// Begin the process of interpreting MFT data
    /// </summary>
    private unsafe Node[] ProcessMft()
    {
        // 64 KB seems to be optimal for Windows XP, Vista is happier with 256KB...
        uint bufferSize =
            (Environment.OSVersion.Version.Major >= 6 ? 256u : 64u) * 1024;

        byte[] data = new byte[bufferSize];

        fixed (byte* buffer = data)
        {
            //Read the $MFT record from disk into memory, which is always the first record in the MFT. 
            ReadFile(buffer, _diskInfo.BytesPerMftRecord, _diskInfo.MftStartLcn * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster);

            //Fixup the raw data from disk. This will also test if it's a valid $MFT record.
            FixupRawMftdata(buffer, _diskInfo.BytesPerMftRecord);

            var mftStreams = new List<Stream>();

            if ((_retrieveMode & RetrieveMode.StandardInformations) == RetrieveMode.StandardInformations)
                _standardInformations = new StandardInformation[1]; //allocate some space for $MFT record

            if (!ProcessMftRecord(buffer, _diskInfo.BytesPerMftRecord, 0, out Node mftNode, mftStreams, true))
                throw new Exception("Can't interpret MFT Record");

            //the bitmap data contains all used inodes on the disk
            _bitmapData =
                ProcessBitmapData(mftStreams);

            OnBitmapDataAvailable();

            Stream dataStream = SearchStream(mftStreams, AttributeType.AttributeData);

            uint maxInode = (uint)_bitmapData.Length * 8;
            if (maxInode > (uint)(dataStream.Size / _diskInfo.BytesPerMftRecord))
                maxInode = (uint)(dataStream.Size / _diskInfo.BytesPerMftRecord);

            Node[] nodes = new Node[maxInode];
            nodes[0] = mftNode;

            if ((_retrieveMode & RetrieveMode.StandardInformations) == RetrieveMode.StandardInformations)
            {
                StandardInformation mftRecordInformation = _standardInformations[0];
                _standardInformations = new StandardInformation[maxInode];
                _standardInformations[0] = mftRecordInformation;
            }

            if ((_retrieveMode & RetrieveMode.Streams) == RetrieveMode.Streams)
                _streams = new Stream[maxInode][];

            /* Read and process all the records in the MFT. The records are read into a
               buffer and then given one by one to the InterpretMftRecord() subroutine. */

            ulong BlockStart = 0, BlockEnd = 0;
            ulong RealVcn = 0, Vcn = 0;

            ulong totalBytesRead = 0;
            int fragmentIndex = 0;
            int fragmentCount = dataStream.Fragments.Count;
            for (uint nodeIndex = 1; nodeIndex < maxInode; nodeIndex++)
            {
                // Ignore the Inode if the bitmap says it's not in use.
                if ((_bitmapData[nodeIndex >> 3] & BitmapMasks[nodeIndex % 8]) == 0)
                    continue;

                if (nodeIndex >= BlockEnd)
                {
                    if (!ReadNextChunk(
                            buffer,
                            bufferSize, 
                            nodeIndex, 
                            fragmentIndex,
                            dataStream, 
                            ref BlockStart, 
                            ref BlockEnd, 
                            ref Vcn, 
                            ref RealVcn))
                        break;

                    totalBytesRead += (BlockEnd - BlockStart) * _diskInfo.BytesPerMftRecord;
                }

                FixupRawMftdata(
                        buffer + (nodeIndex - BlockStart) * _diskInfo.BytesPerMftRecord,
                        _diskInfo.BytesPerMftRecord
                    );

                List<Stream> streams = null;
                if ((_retrieveMode & RetrieveMode.Streams) == RetrieveMode.Streams)
                    streams = [];

                if (!ProcessMftRecord(
                        buffer + (nodeIndex - BlockStart) * _diskInfo.BytesPerMftRecord,
                        _diskInfo.BytesPerMftRecord,
                        nodeIndex,
                        out Node newNode,
                        streams,
                        false))
                    continue;

                nodes[nodeIndex] = newNode;

                if (streams != null)
                    _streams[nodeIndex] = [.. streams];
            }

            return nodes;
        }
    }
}
