using System.Collections.Generic;

namespace System.IO.Filesystem.Ntfs;

sealed class Stream(int nameIndex, AttributeType type, ulong size)
{
    public ulong Clusters;                      // Total number of clusters.
    public ulong Size = size;                   // Total number of bytes.
    public AttributeType Type = type;
    public int NameIndex = nameIndex;
    public List<Fragment> _fragments;

    public List<Fragment> Fragments
        => _fragments ??= new List<Fragment>(5);
}