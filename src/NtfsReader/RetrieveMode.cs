namespace System.IO.Filesystem.Ntfs;

/// <summary>
/// Allow one to retrieve only needed information to reduce memory footprint.
/// </summary>
[Flags]
public enum RetrieveMode
{
    /// <summary>
    /// Includes the name, size, attributes and hierarchical information only.
    /// </summary>
    Minimal = 0,

    /// <summary>
    /// Retrieve the lastModified, lastAccessed and creationTime.
    /// </summary>
    StandardInformations = 1,

    /// <summary>
    /// Retrieve file's streams information.
    /// </summary>
    Streams = 2,

    /// <summary>
    /// Retrieve file's fragments information.
    /// </summary>
    Fragments = 4,

    /// <summary>
    /// Retrieve all information available.
    /// </summary>
    All = StandardInformations | Streams | Fragments,
}
