using System.Collections.Generic;
using System.Text;

namespace System.IO.Filesystem.Ntfs;

public partial class NtfsReader
{
    /// <summary>
    /// Recurse the node hierarchy and construct its entire name
    /// stopping at the root directory.
    /// </summary>
    private string GetNodeFullNameCore(uint nodeIndex)
    {
        uint node = nodeIndex;

        var fullPathNodes = new Stack<uint>();
        fullPathNodes.Push(node);

        uint lastNode = node;
        while (true)
        {
            uint parent = _nodes[node].ParentNodeIndex;

            // Loop until we reach the root directory
            if (parent == ROOTDIRECTORY)
                break;

            if (parent == lastNode)
                throw new InvalidDataException("Detected a loop in the tree structure.");

            fullPathNodes.Push(parent);

            lastNode = node;
            node = parent;
        }

        StringBuilder fullPath = new StringBuilder();
        fullPath.Append(_driveInfo.Name.TrimEnd(['\\']));

        while (fullPathNodes.Count > 0)
        {
            node = fullPathNodes.Pop();

            fullPath.Append(@"\");
            fullPath.Append(GetNameFromIndex(_nodes[node].NameIndex));
        }

        return fullPath.ToString();
    }
}
