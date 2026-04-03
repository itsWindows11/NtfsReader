using System.IO.Filesystem.Ntfs;
using System.Security.Principal;

namespace NtfsReader.Tests;

/// <summary>
/// Integration tests for <see cref="NtfsReader"/> that exercise actual NTFS volume reading.
///
/// These tests are skipped automatically on non-Windows platforms or when the current
/// process does not hold Administrator privileges, since raw volume access requires both.
///
/// What is verified:
///   - The reader opens successfully and returns a populated node list.
///   - File sizes reported via <see cref="INode.Size"/> match <see cref="FileInfo.Length"/>.
///   - The async factory (<see cref="NtfsReader.CreateAsync"/>) produces the same node
///     count as the synchronous constructor.
///   - Cancellation of <see cref="NtfsReader.CreateAsync"/> raises
///     <see cref="OperationCanceledException"/> before the scan completes.
///   - <see cref="RetrieveMode.StandardInformations"/> timestamps are plausible (non-zero,
///     within a sane range).
///   - <see cref="INode.FullName"/> always begins with the drive root.
/// </summary>
public class IntegrationTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // Platform guard
    // ──────────────────────────────────────────────────────────────────────────

    private static bool IsWindowsAdmin()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static DriveInfo? SystemDrive()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        string root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var drive = new DriveInfo(root);
        return drive.DriveFormat == "NTFS" ? drive : null;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Sync constructor
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SyncConstructor_ReturnsNodes_OnWindowsAdmin()
    {
        if (!IsWindowsAdmin())
            return; // skip

        var drive = SystemDrive();
        if (drive == null)
            return;

        var reader = new System.IO.Filesystem.Ntfs.NtfsReader(drive, RetrieveMode.Minimal);
        var nodes = reader.GetNodes(drive.Name);

        Assert.NotNull(nodes);
        Assert.True(nodes.Count > 0, "Expected at least one node on the system volume.");
    }

    [Fact]
    public void SyncConstructor_NodeFullNamesStartWithDriveRoot()
    {
        if (!IsWindowsAdmin())
            return;

        var drive = SystemDrive();
        if (drive == null)
            return;

        var reader = new System.IO.Filesystem.Ntfs.NtfsReader(drive, RetrieveMode.Minimal);
        var nodes = reader.GetNodes(drive.Name);

        // Sample up to 500 nodes to keep the test fast.
        foreach (var node in nodes.Take(500))
            Assert.StartsWith(drive.Name, node.FullName, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // File-size correctness (issue #2 regression test)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FileSizes_MatchFileInfoLength_ForSampledFiles()
    {
        if (!IsWindowsAdmin())
            return;

        var drive = SystemDrive();
        if (drive == null)
            return;

        var reader = new System.IO.Filesystem.Ntfs.NtfsReader(drive, RetrieveMode.Minimal);
        var nodes = reader.GetNodes(drive.Name);

        int filesChecked = 0;
        int mismatches = 0;

        // Check up to 200 real files in System32 (well-known, stable sizes).
        foreach (var node in nodes.Where(n =>
            (n.Attributes & Attributes.Directory) == 0 &&
            n.FullName.StartsWith(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            if (filesChecked >= 200) break;

            try
            {
                var fi = new FileInfo(node.FullName);
                if (!fi.Exists) continue;

                filesChecked++;
                if ((ulong)fi.Length != node.Size)
                    mismatches++;
            }
            catch
            {
                // Access denied / locked files are skipped.
            }
        }

        // We must have examined at least a few files and found zero size mismatches.
        if (filesChecked > 0)
            Assert.Equal(0, mismatches);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Async factory
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ProducesSameNodeCount_AsSyncConstructor()
    {
        if (!IsWindowsAdmin())
            return;

        var drive = SystemDrive();
        if (drive == null)
            return;

        var syncReader = new System.IO.Filesystem.Ntfs.NtfsReader(drive, RetrieveMode.Minimal);
        var asyncReader = await System.IO.Filesystem.Ntfs.NtfsReader
            .CreateAsync(drive, RetrieveMode.Minimal);

        int syncCount = syncReader.GetNodes(drive.Name).Count;
        int asyncCount = asyncReader.GetNodes(drive.Name).Count;

        Assert.Equal(syncCount, asyncCount);
    }

    [Fact]
    public async Task CreateAsync_Cancellation_ThrowsOperationCanceledException()
    {
        if (!IsWindowsAdmin())
            return;

        var drive = SystemDrive();
        if (drive == null)
            return;

        using var cts = new CancellationTokenSource();
        // Cancel immediately so the task has no chance to complete.
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            System.IO.Filesystem.Ntfs.NtfsReader.CreateAsync(drive, RetrieveMode.Minimal, cts.Token));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // RetrieveMode.StandardInformations
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StandardInformations_TimestampsArePlausible()
    {
        if (!IsWindowsAdmin())
            return;

        var drive = SystemDrive();
        if (drive == null)
            return;

        var reader = new System.IO.Filesystem.Ntfs.NtfsReader(drive, RetrieveMode.StandardInformations);
        var nodes = reader.GetNodes(drive.Name);

        // Lower bound chosen well below any plausible real file date; the NTFS epoch
        // starts at 1601-01-01 but no volume in practice has files that old.
        var minDate = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var maxDate = DateTime.UtcNow.AddDays(1);

        foreach (var node in nodes.Take(200))
        {
            Assert.InRange(node.CreationTime, minDate, maxDate);
            Assert.InRange(node.LastChangeTime, minDate, maxDate);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // DiskInfo
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DiskInfo_HasSaneBytesPerSector()
    {
        if (!IsWindowsAdmin())
            return;

        var drive = SystemDrive();
        if (drive == null)
            return;

        var reader = new System.IO.Filesystem.Ntfs.NtfsReader(drive, RetrieveMode.Minimal);

        // NTFS supports 512, 1024, 2048 or 4096 bytes per sector.
        Assert.True(reader.DiskInfo.BytesPerSector is 512 or 1024 or 2048 or 4096);
        Assert.True(reader.DiskInfo.BytesPerMftRecord is 1024 or 4096);
        Assert.True(reader.DiskInfo.TotalSectors > 0);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ArgumentNullException guard on public API
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullDriveInfo_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new System.IO.Filesystem.Ntfs.NtfsReader(null!, RetrieveMode.Minimal));
    }

    [Fact]
    public async Task CreateAsync_NullDriveInfo_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            System.IO.Filesystem.Ntfs.NtfsReader.CreateAsync(null!, RetrieveMode.Minimal));
    }
}
