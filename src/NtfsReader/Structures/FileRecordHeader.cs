using System.Runtime.InteropServices;

namespace System.IO.Filesystem.Ntfs;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct FileRecordHeader
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