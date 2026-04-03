using System.IO.Filesystem.Ntfs;
using System.Reflection;

namespace NtfsReader.Tests;

/// <summary>
/// Unit tests for the NtfsReader name-string table and node-index helpers.
///
/// The name table is a shared string-interning structure used to minimise allocations
/// when millions of file names are read from the MFT.  Key invariants:
///   - Index 0 is the reserved "no name" sentinel; GetNameFromIndex(0) must return null.
///   - Every unique name gets exactly one index ≥ 1.
///   - Duplicate names reuse the existing index (no duplication in _names).
///   - GetNameFromIndex round-trips every name inserted via GetNameIndex.
/// </summary>
public class NameTableTests
{
    // Create a bare NtfsReader instance with no I/O, purely to test the internal
    // name-table methods (exposed as internal via InternalsVisibleTo).
    private static System.IO.Filesystem.Ntfs.NtfsReader CreateBare()
    {
        // Use the private parameterless constructor via reflection.
        var ctor = typeof(System.IO.Filesystem.Ntfs.NtfsReader)
            .GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null)
            ?? throw new InvalidOperationException("Private NtfsReader() not found.");
        return (System.IO.Filesystem.Ntfs.NtfsReader)ctor.Invoke(null);
    }

    [Fact]
    public void Index0_IsReservedSentinel_ReturnsNull()
    {
        var reader = CreateBare();
        // Before any names are added, index 0 must already return null.
        Assert.Null(reader.GetNameFromIndex(0));
    }

    [Fact]
    public void FirstRealName_GetsIndexGreaterThanZero()
    {
        var reader = CreateBare();

        // Use reflection to call the private GetNameIndex.
        int idx = GetNameIndex(reader, "hello");

        Assert.True(idx > 0, "First real name must not get the reserved index 0.");
    }

    [Fact]
    public void GetNameFromIndex_RoundTrips()
    {
        var reader = CreateBare();

        int idx = GetNameIndex(reader, "Windows");

        Assert.Equal("Windows", reader.GetNameFromIndex(idx));
    }

    [Fact]
    public void DuplicateName_ReturnsSameIndex()
    {
        var reader = CreateBare();

        int idx1 = GetNameIndex(reader, "System32");
        int idx2 = GetNameIndex(reader, "System32");

        Assert.Equal(idx1, idx2);
    }

    [Fact]
    public void MultipleDistinctNames_GetDistinctIndices()
    {
        var reader = CreateBare();

        int idx1 = GetNameIndex(reader, "alpha");
        int idx2 = GetNameIndex(reader, "beta");
        int idx3 = GetNameIndex(reader, "gamma");

        Assert.NotEqual(idx1, idx2);
        Assert.NotEqual(idx2, idx3);
        Assert.NotEqual(idx1, idx3);
    }

    [Fact]
    public void GetNameFromIndex_AllNamesRoundTrip()
    {
        var reader = CreateBare();
        string[] names = ["ntdll.dll", "kernel32.dll", "user32.dll", "combase.dll", "advapi32.dll"];

        var indices = names.Select(n => GetNameIndex(reader, n)).ToArray();

        for (int i = 0; i < names.Length; i++)
            Assert.Equal(names[i], reader.GetNameFromIndex(indices[i]));
    }

    [Fact]
    public void LargeNumberOfNames_NoCollisions()
    {
        var reader = CreateBare();
        int count = 10_000;
        var indices = new Dictionary<string, int>(count);

        for (int i = 0; i < count; i++)
        {
            string name = $"file_{i:D6}.dat";
            indices[name] = GetNameIndex(reader, name);
        }

        // Verify no two distinct names share an index, and all round-trip.
        var uniqueIndices = new HashSet<int>();
        foreach (var (name, idx) in indices)
        {
            Assert.True(uniqueIndices.Add(idx), $"Index {idx} used by more than one name.");
            Assert.Equal(name, reader.GetNameFromIndex(idx));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static readonly MethodInfo s_getNameIndex =
        typeof(System.IO.Filesystem.Ntfs.NtfsReader)
            .GetMethod("GetNameIndex", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("GetNameIndex not found.");

    private static int GetNameIndex(System.IO.Filesystem.Ntfs.NtfsReader reader, string name)
        => (int)s_getNameIndex.Invoke(reader, [name])!;
}
