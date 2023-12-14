using System.Collections.Generic;

namespace System.IO.Filesystem.Ntfs;

/// <summary>
/// Adds some functionality to the basic node.
/// </summary>
struct NodeWrapper(NtfsReader reader, uint nodeIndex, Node node) : INode
{
    string _fullName;

    public readonly uint NodeIndex => nodeIndex;

    public readonly uint ParentNodeIndex => node.ParentNodeIndex;

    public readonly Attributes Attributes => node.Attributes;

    public readonly string Name => reader.GetNameFromIndex(node.NameIndex);

    public readonly ulong Size => node.Size;

    public string FullName => _fullName ??= reader.GetNodeFullNameCore(nodeIndex);

    public readonly IList<IStream> Streams
    {
        get
        {
            if (reader._streams == null)
                throw new InvalidOperationException("The streams haven't been retrieved. Make sure to use the proper RetrieveMode.");

            Stream[] streams = reader._streams[nodeIndex];
            if (streams == null)
                return null;

            var newStreams = new List<IStream>();

            for (int i = 0; i < streams.Length; ++i)
                newStreams.Add(new StreamWrapper(reader, this, i));

            return newStreams;
        }
    }

    public readonly DateTime CreationTime
    {
        get
        {
            if (reader._standardInformations == null)
                throw new NotSupportedException("The StandardInformation haven't been retrieved. Make sure to use the proper RetrieveMode.");

            return DateTime.FromFileTimeUtc((long)reader._standardInformations[nodeIndex].CreationTime);
        }
    }

    public readonly DateTime LastChangeTime
    {
        get
        {
            if (reader._standardInformations == null)
                throw new NotSupportedException("The StandardInformation haven't been retrieved. Make sure to use the proper RetrieveMode.");

            return DateTime.FromFileTimeUtc((long)reader._standardInformations[nodeIndex].LastChangeTime);
        }
    }

    public readonly DateTime LastAccessTime
    {
        get
        {
            if (reader._standardInformations == null)
                throw new NotSupportedException("The StandardInformation haven't been retrieved. Make sure to use the proper RetrieveMode.");

            return DateTime.FromFileTimeUtc((long)reader._standardInformations[nodeIndex].LastAccessTime);
        }
    }

    public override readonly string ToString()
    {
        return $"Name: {Name}, Size: {Size / 1024} KB";
    }
}