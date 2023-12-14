using System.Runtime.InteropServices;

namespace System.IO.Filesystem.Ntfs;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct AttributeFileName
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