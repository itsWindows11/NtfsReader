using System.Runtime.InteropServices;

namespace System.IO.Filesystem.Ntfs;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct AttributeStandardInformation
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