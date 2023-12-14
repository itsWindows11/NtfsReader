using System.Runtime.InteropServices;

namespace System.IO.Filesystem.Ntfs;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct RecordHeader
{
    public RecordType Type;                  /* File type, for example 'FILE' */
    public ushort UsaOffset;             /* Offset to the Update Sequence Array */
    public ushort UsaCount;              /* Size in words of Update Sequence Array */
    public ulong Lsn;                   /* $LogFile Sequence Number (LSN) */
}