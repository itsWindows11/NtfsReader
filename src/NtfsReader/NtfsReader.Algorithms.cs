using System.Collections.Generic;
using System.Text;

namespace System.IO.Filesystem.Ntfs;

public partial class NtfsReader
{
    /// <summary>
    /// Recursively walks the node hierarchy and returns the fully-qualified path of the node
    /// identified by <paramref name="nodeIndex"/>, stopping when the root directory is reached.
    /// </summary>
    /// <param name="nodeIndex">Zero-based index into the internal node array.</param>
    /// <returns>The full path, e.g. <c>C:\Windows\System32\ntdll.dll</c>.</returns>
    /// <exception cref="InvalidDataException">
    /// A parent-child loop was detected, indicating MFT corruption.
    /// </exception>
    internal string GetNodeFullNameCore(uint nodeIndex)
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
