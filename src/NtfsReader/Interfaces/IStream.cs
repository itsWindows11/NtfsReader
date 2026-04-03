using System.Collections.Generic;

namespace System.IO.Filesystem.Ntfs;

/// <summary>
/// In NTFS every node may have one or more named or unnamed data streams.
/// The unnamed stream (name is <see langword="null"/>) holds the file's primary content;
/// named streams are Alternate Data Streams (ADS).
/// </summary>
public interface IStream
{
    /// <summary>
    /// Gets the name of the stream, or <see langword="null"/> for the unnamed main stream.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the logical size of the stream in bytes (the <c>DataSize</c> field of the
    /// NTFS <c>$DATA</c> attribute, i.e. the number of valid bytes, not the allocated size).
    /// </summary>
    ulong Size { get; }

    /// <summary>
    /// Gets the ordered list of disk extents (fragments) that make up this stream.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="RetrieveMode.Fragments"/> was not specified when the
    /// <see cref="NtfsReader"/> was created.
    /// </exception>
    IList<IFragment> Fragments { get; }
}
