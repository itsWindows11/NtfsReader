using System.IO.Filesystem.Ntfs;

var driveInfo = new DriveInfo("C:\\");
int i = 0;

// Enumerate nodes in the C:\ directory directly, retrieving standard information for each entry.
await foreach (var entry in NtfsReader.EnumerateNodesAsync(driveInfo, RetrieveMode.StandardInformations, "C:\\"))
{
    var isDirectory = entry.Attributes.HasFlag(Attributes.Directory);
    Console.WriteLine($"Name: {entry.Name} ({(isDirectory ? "directory" : "file")}), Size: {entry.Size} bytes");

    i++;

    if (i % 30 == 0)
    {
        Console.WriteLine("Pausing for 1 second...");
        await Task.Delay(1000);
    }

    if (i >= 60)
    {
        Console.WriteLine("Stopping after 60 entries.");
        break;
    }
}

i = 0;

Console.WriteLine("---------------------- Finished enumerating direct entries ----------------------");

// Regular NtfsReader usage (async)
var ntfsReader = await NtfsReader.CreateAsync(driveInfo, RetrieveMode.StandardInformations);

// Enumerate nodes in the C:\ directory directly, retrieving standard information for each entry.
foreach (var entry in ntfsReader.GetNodes("C:\\"))
{
    var isDirectory = entry.Attributes.HasFlag(Attributes.Directory);
    Console.WriteLine($"Name: {entry.Name} ({(isDirectory ? "directory" : "file")}), Size: {entry.Size} bytes");

    i++;

    if (i % 30 == 0)
    {
        Console.WriteLine("Pausing for 1 second...");
        await Task.Delay(1000);
    }

    if (i >= 60)
    {
        Console.WriteLine("Stopping after 60 entries.");
        break;
    }
}

i = 0;

Console.WriteLine("------- Finished enumerating entries using async NtfsReader initialization -------");

// Regular NtfsReader usage (sync)
var ntfsReaderSync = new NtfsReader(driveInfo, RetrieveMode.StandardInformations);

// Enumerate nodes in the C:\ directory directly, retrieving standard information for each entry.
foreach (var entry in ntfsReaderSync.GetNodes("C:\\"))
{
    var isDirectory = entry.Attributes.HasFlag(Attributes.Directory);
    Console.WriteLine($"Name: {entry.Name} ({(isDirectory ? "directory" : "file")}), Size: {entry.Size} bytes");

    i++;

    if (i % 30 == 0)
    {
        Console.WriteLine("Pausing for 1 second...");
        await Task.Delay(1000);
    }

    if (i >= 60)
    {
        Console.WriteLine("Stopping after 60 entries.");
        break;
    }
}

i = 0;

Console.WriteLine("------- Finished enumerating entries using sync NtfsReader initialization -------");