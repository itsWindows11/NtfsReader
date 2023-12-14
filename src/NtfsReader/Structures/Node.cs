namespace System.IO.Filesystem.Ntfs;

/// <summary>
/// Node struct for file and directory entries
/// </summary>
/// <remarks>
/// We keep this as small as possible to reduce footprint for large volume.
/// </remarks>
struct Node
{
    public Attributes Attributes;
    public uint ParentNodeIndex;
    public ulong Size;
    public int NameIndex;
}