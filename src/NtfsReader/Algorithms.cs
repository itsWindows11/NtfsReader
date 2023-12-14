using System.Collections.Generic;

namespace System.IO.Filesystem.Ntfs;

public static class Algorithms
{
    public static IDictionary<uint, List<INode>> AggregateByFragments(IEnumerable<INode> nodes, uint minimumFragments)
    {
        var fragmentsAggregate = new Dictionary<uint, List<INode>>();

        foreach (INode node in nodes)
        {
            IList<IStream> streams = node.Streams;

            if (streams == null || streams.Count == 0)
                continue;

            IList<IFragment> fragments = streams[0].Fragments;

            if (fragments == null)
                continue;

            uint fragmentCount = (uint)fragments.Count;

            if (fragmentCount < minimumFragments)
                continue;

            fragmentsAggregate.TryGetValue(fragmentCount, out List<INode> nodeList);

            if (nodeList == null)
            {
                nodeList = [];
                fragmentsAggregate[fragmentCount] = nodeList;
            }

            nodeList.Add(node);
        }

        return fragmentsAggregate;
    }

    public static IDictionary<ulong, List<INode>> AggregateBySize(IEnumerable<INode> nodes, ulong minimumSize)
    {
        var sizeAggregate = new Dictionary<ulong, List<INode>>();

        foreach (INode node in nodes)
        {
            if ((node.Attributes & Attributes.Directory) != 0 || node.Size < minimumSize)
                continue;

            sizeAggregate.TryGetValue(node.Size, out List<INode> nodeList);

            if (nodeList == null)
            {
                nodeList = [];
                sizeAggregate[node.Size] = nodeList;
            }

            nodeList.Add(node);
        }

        return sizeAggregate;
    }
}
