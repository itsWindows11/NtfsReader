using System.Collections.Generic;

namespace System.IO.Filesystem.Ntfs;

/// <summary>
/// Adds some functionality to the basic stream.
/// </summary>
struct StreamWrapper(NtfsReader reader, NodeWrapper parentNode, int streamIndex) : IStream
{
    public readonly string Name
        => reader.GetNameFromIndex(reader._streams[parentNode.NodeIndex][streamIndex].NameIndex);

    public readonly ulong Size
        => reader._streams[parentNode.NodeIndex][streamIndex].Size;

    public readonly IList<IFragment> Fragments
    {
        get
        {
            if (!reader._retrieveMode.HasFlag(RetrieveMode.Fragments))
                throw new InvalidOperationException("The fragments haven't been retrieved. Make sure to use the proper RetrieveMode.");

            IList<Fragment> fragments =
                reader._streams[parentNode.NodeIndex][streamIndex].Fragments;

            if (fragments == null || fragments.Count == 0)
                return null;

            var newFragments = new List<IFragment>();

            foreach (Fragment fragment in fragments)
                newFragments.Add(new FragmentWrapper(fragment));

            return newFragments;
        }
    }
}