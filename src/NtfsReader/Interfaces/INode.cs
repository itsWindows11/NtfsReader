using System.Collections.Generic;

namespace System.IO.Filesystem.Ntfs;

/// <summary>
/// Directory & Files Information are stored in inodes
/// </summary>
public interface INode
{
    Attributes Attributes { get; }

    uint NodeIndex { get; }

    uint ParentNodeIndex { get; }

    string Name { get; }

    ulong Size { get; }

    string FullName { get; }

    IList<IStream> Streams { get; }

    DateTime CreationTime { get; }

    DateTime LastChangeTime { get; }

    DateTime LastAccessTime { get; }
}
