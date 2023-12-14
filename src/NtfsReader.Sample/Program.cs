using System.IO.Filesystem.Ntfs;

var ntfsReader = new NtfsReader(new DriveInfo("C:\\"), RetrieveMode.Minimal);

var nodes = ntfsReader.GetNodes("C:\\");

foreach (INode node in nodes)
{
    Console.WriteLine(node.FullName);
}