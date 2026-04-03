# NtfsReader

A fast, .NET Standard 2.0 library for reading the **Master File Table (MFT)** of NTFS volumes. Scanning the MFT directly is orders of magnitude faster than conventional directory enumeration (`Directory.EnumerateFiles`), making it ideal for disk analysis, file indexing, and search tools.

> **Forked from** <https://sourceforge.net/projects/ntfsreader> (unmaintained). This fork modernises the code, fixes correctness bugs, and adds async support.

---

## Requirements

- Windows (NTFS volumes are Windows-only)
- **.NET Standard 2.0** compatible runtime (.NET Framework 4.6.1+, .NET Core 2.0+, .NET 5+)
- **Administrator** privileges — raw volume access requires an elevated process

---

## Installation

```
dotnet add package itsWindows11.NtfsReader
```

---

## Quick start

### Synchronous

```csharp
using System.IO;
using System.IO.Filesystem.Ntfs;

var drive  = new DriveInfo("C:\\");
var reader = new NtfsReader(drive, RetrieveMode.Minimal);

foreach (INode node in reader.GetNodes("C:\\"))
    Console.WriteLine($"{node.FullName}  ({node.Size:N0} bytes)");
```

### Asynchronous (recommended for UI / ASP.NET)

```csharp
// Opens the volume with FILE_FLAG_OVERLAPPED and reads via FileStream.ReadAsync,
// so every I/O operation is a true kernel async call.  The calling thread is never
// blocked on disk I/O.
var reader = await NtfsReader.CreateAsync(drive, RetrieveMode.Minimal, cancellationToken);

var nodes = reader.GetNodes("C:\\");
```

---

## API reference

### `NtfsReader`

| Member | Description |
|--------|-------------|
| `NtfsReader(DriveInfo, RetrieveMode)` | Synchronously reads the entire MFT. Blocks the calling thread. |
| `static Task<NtfsReader> CreateAsync(DriveInfo, RetrieveMode, CancellationToken)` | Truly async MFT read. Opens the volume with `FILE_FLAG_OVERLAPPED` and issues async kernel I/O via `FileStream.ReadAsync`. |
| `IDiskInfo DiskInfo` | Geometry of the scanned volume (sector size, cluster size, MFT location, …). |
| `List<INode> GetNodes(string rootPath)` | Returns all nodes whose full path starts with `rootPath`. Runs in parallel. |
| `byte[] GetVolumeBitmap()` | Raw cluster allocation bitmap (one bit per cluster). |
| `event EventHandler BitmapDataAvailable` | Raised as soon as the bitmap has been read (before the main MFT scan). |

### `RetrieveMode` flags

| Flag | What it loads | Extra memory cost |
|------|--------------|-------------------|
| `Minimal` | Name, size, attributes, parent index | Lowest |
| `StandardInformations` | Creation / last-access / last-change timestamps | ~24 bytes per node |
| `Streams` | All data streams (including Alternate Data Streams) | Varies |
| `Fragments` | Per-stream disk extent list | Varies |
| `All` | Everything above | Highest |

> **Tip:** `Streams` and `Fragments` can add hundreds of MB of RAM usage on large volumes. Only request what you need.

### `INode`

| Property | Description |
|----------|-------------|
| `NodeIndex` | Zero-based MFT record index. |
| `ParentNodeIndex` | `NodeIndex` of the containing directory. |
| `Name` | Bare file/directory name (no path). |
| `FullName` | Fully-qualified path, e.g. `C:\Windows\System32\ntdll.dll`. |
| `Size` | Logical size of the **unnamed data stream** in bytes (the file's primary content). Directories are 0. |
| `Attributes` | Standard NTFS attributes (`ReadOnly`, `Hidden`, `Directory`, `Compressed`, …). |
| `CreationTime` | Requires `RetrieveMode.StandardInformations`. |
| `LastChangeTime` | Requires `RetrieveMode.StandardInformations`. |
| `LastAccessTime` | Requires `RetrieveMode.StandardInformations`. |
| `Streams` | Requires `RetrieveMode.Streams`. |

### `IStream`

| Property | Description |
|----------|-------------|
| `Name` | Stream name, or `null` for the unnamed main data stream. |
| `Size` | Logical byte count of the stream (`DataSize` from the NTFS `$DATA` attribute). |
| `Fragments` | Disk extents; requires `RetrieveMode.Fragments`. |

---

## Known limitations

- **Alternate Data Streams** are exposed via `INode.Streams` when `RetrieveMode.Streams` is set, but `INode.Size` always reflects only the *unnamed* (primary) data stream, which is the standard file size.
- **AttributeAttributeList** (files with MFT extension records) is not fully processed; very fragmented files whose attributes span multiple MFT records may show incomplete data.
- **48-bit inode numbers** are not supported to keep the per-node memory footprint small.
- The library is **Windows-only**: it P/Invokes `CreateFile`, `ReadFile`, and `GetVolumeNameForVolumeMountPoint` from `kernel32`.

---

## How it works

1. `CreateFile` opens the raw volume device (e.g. `\\?\Volume{...}`) — or with `FILE_FLAG_OVERLAPPED` for the async path.
2. The boot sector is read to locate the MFT start LCN and derive geometry parameters.
3. The MFT bitmap (`$Bitmap`) is loaded to identify which MFT records are in use.
4. MFT records are read in large sequential chunks (256 KB on Vista+, 64 KB on XP) to amortise seek overhead, and each record is processed in-place.
5. A shared string-interning table deduplicates directory names to minimise heap pressure.
6. `GetNodes` filters the resulting array in parallel using `Parallel.For`.
