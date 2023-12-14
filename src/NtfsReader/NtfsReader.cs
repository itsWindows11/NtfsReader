using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;

namespace System.IO.Filesystem.Ntfs;

public sealed partial class NtfsReader : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct BootSector
    {
        fixed byte AlignmentOrReserved1[3];
        public ulong Signature;
        public ushort BytesPerSector;
        public byte SectorsPerCluster;
        fixed byte AlignmentOrReserved2[26];
        public ulong TotalSectors;
        public ulong MftStartLcn;
        public ulong Mft2StartLcn;
        public uint ClustersPerMftRecord;
        public uint ClustersPerIndexRecord;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VolumeData
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

    private enum RecordType : uint
    {
        File = 0x454c4946,  //'FILE' in ASCII
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RecordHeader
    {
        public RecordType Type;                  /* File type, for example 'FILE' */
        public ushort UsaOffset;             /* Offset to the Update Sequence Array */
        public ushort UsaCount;              /* Size in words of Update Sequence Array */
        public ulong Lsn;                   /* $LogFile Sequence Number (LSN) */
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct INodeReference
    {
        public uint InodeNumberLowPart;
        public ushort InodeNumberHighPart;
        public ushort SequenceNumber;
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct FileRecordHeader
    {
        public RecordHeader RecordHeader;
        public ushort SequenceNumber;        /* Sequence number */
        public ushort LinkCount;             /* Hard link count */
        public ushort AttributeOffset;       /* Offset to the first Attribute */
        public ushort Flags;                 /* Flags. bit 1 = in use, bit 2 = directory, bit 4 & 8 = unknown. */
        public uint BytesInUse;             /* Real size of the FILE record */
        public uint BytesAllocated;         /* Allocated size of the FILE record */
        public INodeReference BaseFileRecord;     /* File reference to the base FILE record */
        public ushort NextAttributeNumber;   /* Next Attribute Id */
        public ushort Padding;               /* Align to 4 UCHAR boundary (XP) */
        public uint MFTRecordNumber;        /* Number of this MFT Record (XP) */
        public ushort UpdateSeqNum;          /*  */
    };

    private enum AttributeType : uint
    {
        AttributeInvalid = 0x00,         /* Not defined by Windows */
        AttributeStandardInformation = 0x10,
        AttributeAttributeList = 0x20,
        AttributeFileName = 0x30,
        AttributeObjectId = 0x40,
        AttributeSecurityDescriptor = 0x50,
        AttributeVolumeName = 0x60,
        AttributeVolumeInformation = 0x70,
        AttributeData = 0x80,
        AttributeIndexRoot = 0x90,
        AttributeIndexAllocation = 0xA0,
        AttributeBitmap = 0xB0,
        AttributeReparsePoint = 0xC0,         /* Reparse Point = Symbolic link */
        AttributeEAInformation = 0xD0,
        AttributeEA = 0xE0,
        AttributePropertySet = 0xF0,
        AttributeLoggedUtilityStream = 0x100
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Attribute
    {
        public AttributeType AttributeType;
        public uint Length;
        public byte Nonresident;
        public byte NameLength;
        public ushort NameOffset;
        public ushort Flags;              /* 0x0001 = Compressed, 0x4000 = Encrypted, 0x8000 = Sparse */
        public ushort AttributeNumber;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct AttributeList
    {
        public AttributeType AttributeType;
        public ushort Length;
        public byte NameLength;
        public byte NameOffset;
        public ulong LowestVcn;
        public INodeReference FileReferenceNumber;
        public ushort Instance;
        public fixed ushort AlignmentOrReserved[3];
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct AttributeFileName
    {
        public INodeReference ParentDirectory;
        public ulong CreationTime;
        public ulong ChangeTime;
        public ulong LastWriteTime;
        public ulong LastAccessTime;
        public ulong AllocatedSize;
        public ulong DataSize;
        public uint FileAttributes;
        public uint AlignmentOrReserved;
        public byte NameLength;
        public byte NameType;                 /* NTFS=0x01, DOS=0x02 */
        public char Name;
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct AttributeStandardInformation
    {
        public ulong CreationTime;
        public ulong FileChangeTime;
        public ulong MftChangeTime;
        public ulong LastAccessTime;
        public uint FileAttributes;       /* READ_ONLY=0x01, HIDDEN=0x02, SYSTEM=0x04, VOLUME_ID=0x08, ARCHIVE=0x20, DEVICE=0x40 */
        public uint MaximumVersions;
        public uint VersionNumber;
        public uint ClassId;
        public uint OwnerId;                        // NTFS 3.0 only
        public uint SecurityId;                     // NTFS 3.0 only
        public ulong QuotaCharge;                // NTFS 3.0 only
        public ulong Usn;                              // NTFS 3.0 only
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ResidentAttribute
    {
        public Attribute Attribute;
        public uint ValueLength;
        public ushort ValueOffset;
        public ushort Flags;               // 0x0001 = Indexed
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct NonResidentAttribute
    {
        public Attribute Attribute;
        public ulong StartingVcn;
        public ulong LastVcn;
        public ushort RunArrayOffset;
        public byte CompressionUnit;
        public fixed byte AlignmentOrReserved[5];
        public ulong AllocatedSize;
        public ulong DataSize;
        public ulong InitializedSize;
        public ulong CompressedSize;    // Only when compressed
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Fragment(ulong lcn, ulong nextVcn)
    {
        public ulong Lcn = lcn;                // Logical cluster number, location on disk.
        public ulong NextVcn = nextVcn;            // Virtual cluster number of next fragment.
    }

    private sealed class Stream(int nameIndex, AttributeType type, ulong size)
    {
        public ulong Clusters;                      // Total number of clusters.
        public ulong Size = size;                          // Total number of bytes.
        public AttributeType Type = type;
        public int NameIndex = nameIndex;
        public List<Fragment> _fragments;

        public List<Fragment> Fragments
            => _fragments ??= new List<Fragment>(5);
    }

    /// <summary>
    /// Node struct for file and directory entries
    /// </summary>
    /// <remarks>
    /// We keep this as small as possible to reduce footprint for large volume.
    /// </remarks>
    private struct Node
    {
        public Attributes Attributes;
        public uint ParentNodeIndex;
        public ulong Size;
        public int NameIndex;
    }

    /// <summary>
    /// Contains extra information not required for basic purposes.
    /// </summary>
    private struct StandardInformation(ulong creationTime, ulong lastAccessTime, ulong lastChangeTime)
    {
        public ulong CreationTime = creationTime;
        public ulong LastAccessTime = lastAccessTime;
        public ulong LastChangeTime = lastChangeTime;
    }

    /// <summary>
    /// Add some functionality to the basic stream
    /// </summary>
    private struct FragmentWrapper( Fragment fragment) : IFragment
    {
        public readonly ulong Lcn => fragment.Lcn;

        public readonly ulong NextVcn => fragment.NextVcn;
    }

    /// <summary>
    /// Add some functionality to the basic stream
    /// </summary>
    private struct StreamWrapper(NtfsReader reader, NodeWrapper parentNode, int streamIndex) : IStream
    {
        public readonly string Name
            => reader.GetNameFromIndex(reader._streams[parentNode.NodeIndex][streamIndex].NameIndex);

        public readonly ulong Size
            => reader._streams[parentNode.NodeIndex][streamIndex].Size;

        public readonly IList<IFragment> Fragments
        {
            get
            {
                if (!reader._retrieveMode.HasFlag(RetrieveMode.Fragments))
                    throw new InvalidOperationException("The fragments haven't been retrieved. Make sure to use the proper RetrieveMode.");

                IList<Fragment> fragments =
                    reader._streams[parentNode.NodeIndex][streamIndex].Fragments;

                if (fragments == null || fragments.Count == 0)
                    return null;

                var newFragments = new List<IFragment>();

                foreach (Fragment fragment in fragments)
                    newFragments.Add(new FragmentWrapper(fragment));

                return newFragments;
            }
        }
    }

    /// <summary>
    /// Add some functionality to the basic node
    /// </summary>
    private struct NodeWrapper(NtfsReader reader, uint nodeIndex, Node node) : INode
    {
        string _fullName;

        public readonly uint NodeIndex => nodeIndex;

        public readonly uint ParentNodeIndex => node.ParentNodeIndex;

        public readonly Attributes Attributes => node.Attributes;

        public readonly string Name => reader.GetNameFromIndex(node.NameIndex);

        public readonly ulong Size => node.Size;

        public string FullName => _fullName ??= reader.GetNodeFullNameCore(nodeIndex);

        public readonly IList<IStream> Streams
        {
            get 
            {
                if (reader._streams == null)
                    throw new InvalidOperationException("The streams haven't been retrieved. Make sure to use the proper RetrieveMode.");

                Stream[] streams = reader._streams[nodeIndex];
                if (streams == null)
                    return null;

                var newStreams = new List<IStream>();

                for (int i = 0; i < streams.Length; ++i)
                    newStreams.Add(new StreamWrapper(reader, this, i));

                return newStreams;
            }
        }

        public readonly DateTime CreationTime
        {
            get
            {
                if (reader._standardInformations == null)
                    throw new NotSupportedException("The StandardInformation haven't been retrieved. Make sure to use the proper RetrieveMode.");

                return DateTime.FromFileTimeUtc((long)reader._standardInformations[nodeIndex].CreationTime);
            }
        }

        public readonly DateTime LastChangeTime
        {
            get
            {
                if (reader._standardInformations == null)
                    throw new NotSupportedException("The StandardInformation haven't been retrieved. Make sure to use the proper RetrieveMode.");

                return DateTime.FromFileTimeUtc((long)reader._standardInformations[nodeIndex].LastChangeTime);
            }
        }

        public readonly DateTime LastAccessTime
        {
            get
            {
                if (reader._standardInformations == null)
                    throw new NotSupportedException("The StandardInformation haven't been retrieved. Make sure to use the proper RetrieveMode.");

                return DateTime.FromFileTimeUtc((long)reader._standardInformations[nodeIndex].LastAccessTime);
            }
        }
    }

    /// <summary>
    /// Simple structure of available disk informations.
    /// </summary>
    private sealed class DiskInfoWrapper : IDiskInfo
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

    private const ulong VIRTUALFRAGMENT = ulong.MaxValue;
    private const uint ROOTDIRECTORY = 5;

    private readonly byte[] BitmapMasks = [1, 2, 4, 8, 16, 32, 64, 128];

    SafeFileHandle _volumeHandle;
    DiskInfoWrapper _diskInfo;
    Node[] _nodes;
    StandardInformation[] _standardInformations;
    Stream[][] _streams;
    DriveInfo _driveInfo;
    List<string> _names = [];
    RetrieveMode _retrieveMode;
    byte[] _bitmapData;

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
    private string GetNameFromIndex(int nameIndex)
    {
        return nameIndex == 0 ? null : _names[nameIndex];
    }

    private Stream SearchStream(List<Stream> streams, AttributeType streamType)
    {
        //since the number of stream is usually small, we can afford O(n)
        foreach (Stream stream in streams)
            if (stream.Type == streamType)
                return stream;

        return null;
    }

    private Stream SearchStream(List<Stream> streams, AttributeType streamType, int streamNameIndex)
    {
        //since the number of stream is usually small, we can afford O(n)
        foreach (Stream stream in streams)
            if (stream.Type == streamType &&
                stream.NameIndex == streamNameIndex)
                return stream;

        return null;
    }

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
                throw new Exception("Error: datarun is longer than buffer, the MFT may be corrupt.");

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
                throw new Exception("Can't interpret Mft Record");

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
