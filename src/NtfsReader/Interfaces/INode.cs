using System.Collections.Generic;

namespace System.IO.Filesystem.Ntfs;

/// <summary>
/// Represents a single file or directory entry in the NTFS Master File Table.
/// </summary>
public interface INode
{
    /// <summary>
    /// Gets the file-system attributes of this node (read-only, hidden, system, directory, etc.).
    /// </summary>
    Attributes Attributes { get; }

    /// <summary>
    /// Gets the zero-based index of this node within the MFT.
    /// </summary>
    uint NodeIndex { get; }

    /// <summary>
    /// Gets the <see cref="NodeIndex"/> of the directory that directly contains this node.
    /// </summary>
    uint ParentNodeIndex { get; }

    /// <summary>
    /// Gets the bare name of the file or directory (without path), e.g. <c>"ntdll.dll"</c>.
    /// Returns <see langword="null"/> for MFT entries that have no associated name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the size of the file's unnamed data stream in bytes.
    /// Returns 0 for directories and for files whose data is stored inside the MFT record
    /// itself (resident data) with a length of zero.
    /// </summary>
    ulong Size { get; }

    /// <summary>
    /// Gets the fully-qualified path of this node, e.g.
    /// <c>C:\Windows\System32\ntdll.dll</c>.
    /// </summary>
    string FullName { get; }

    /// <summary>
    /// Gets the list of NTFS data streams attached to this node, including the unnamed
    /// main stream and any Alternate Data Streams.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="RetrieveMode.Streams"/> was not specified when the
    /// <see cref="NtfsReader"/> was created.
    /// </exception>
    IList<IStream> Streams { get; }

    /// <summary>
    /// Gets the time the node was created (UTC).
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// <see cref="RetrieveMode.StandardInformations"/> was not specified when the
    /// <see cref="NtfsReader"/> was created.
    /// </exception>
    DateTime CreationTime { get; }

    /// <summary>
    /// Gets the time the node's data or metadata was last changed (UTC).
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// <see cref="RetrieveMode.StandardInformations"/> was not specified when the
    /// <see cref="NtfsReader"/> was created.
    /// </exception>
    DateTime LastChangeTime { get; }

    /// <summary>
    /// Gets the time the node was last accessed (UTC).
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// <see cref="RetrieveMode.StandardInformations"/> was not specified when the
    /// <see cref="NtfsReader"/> was created.
    /// </exception>
    DateTime LastAccessTime { get; }
}
