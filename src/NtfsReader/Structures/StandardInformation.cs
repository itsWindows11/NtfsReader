namespace System.IO.Filesystem.Ntfs;

/// <summary>
/// Contains extra information not required for basic purposes.
/// </summary>
struct StandardInformation(ulong creationTime, ulong lastAccessTime, ulong lastChangeTime)
{
    public ulong CreationTime = creationTime;
    public ulong LastAccessTime = lastAccessTime;
    public ulong LastChangeTime = lastChangeTime;
}