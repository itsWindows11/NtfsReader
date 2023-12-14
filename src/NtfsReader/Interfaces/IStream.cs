using System.Collections.Generic;

namespace System.IO.Filesystem.Ntfs;

/// <summary>
/// In Ntfs, each node may have multiple streams.
/// </summary>
public interface IStream
{
    string Name { get; }

    ulong Size { get; }

    IList<IFragment> Fragments { get; }
}
