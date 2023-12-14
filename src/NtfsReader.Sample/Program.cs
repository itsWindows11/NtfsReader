using System.IO.Filesystem.Ntfs;

var ntfsReader = new NtfsReader(new DriveInfo("C:\\"), RetrieveMode.Minimal);

var nodes = ntfsReader.GetNodes("C:\\");

// Since millions of nodes are returned, printing all of them isn't feasible for the sake of testing. Just print the first 30.
foreach (var node in nodes.Take(30))
{
    Console.WriteLine(node.FullName);
}