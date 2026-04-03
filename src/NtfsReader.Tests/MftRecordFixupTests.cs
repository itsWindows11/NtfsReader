using System.IO.Filesystem.Ntfs;
using System.Reflection;

namespace NtfsReader.Tests;

/// <summary>
/// Unit tests for MFT record fixup (Update Sequence Array application).
///
/// NTFS protects each 512-byte sector within a FILE record by replacing the last two
/// bytes of each sector (offset 510, 1022, …) with the current Update Sequence Number
/// (USN) on write.  On read, the reader must restore the original values stored in the
/// Update Sequence Array (USA) before interpreting the record.
///
/// These tests use the <c>internal</c> helper <c>FixupRawMftdataAt</c> which the library
/// exposes specifically for testing via InternalsVisibleTo.
/// </summary>
[TestClass]
public class MftRecordFixupTests
{
    private static System.IO.Filesystem.Ntfs.NtfsReader CreateBare()
    {
        var ctor = typeof(System.IO.Filesystem.Ntfs.NtfsReader)
            .GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null)!;
        var reader = (System.IO.Filesystem.Ntfs.NtfsReader)ctor.Invoke(null);

        // Inject a minimal DiskInfoWrapper with BytesPerSector = 512 via reflection.
        var diskInfoField = typeof(System.IO.Filesystem.Ntfs.NtfsReader)
            .GetField("_diskInfo", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var wrapper = CreateDiskInfoWrapper(bytesPerSector: 512);
        diskInfoField.SetValue(reader, wrapper);

        return reader;
    }

    private static object CreateDiskInfoWrapper(ushort bytesPerSector)
    {
        var wrapperType = typeof(System.IO.Filesystem.Ntfs.NtfsReader).Assembly
            .GetType("System.IO.Filesystem.Ntfs.DiskInfoWrapper")!;
        var wrapper = Activator.CreateInstance(wrapperType)!;
        wrapperType.GetField("BytesPerSector")!.SetValue(wrapper, bytesPerSector);
        return wrapper;
    }

    private static readonly MethodInfo s_fixupAt =
        typeof(System.IO.Filesystem.Ntfs.NtfsReader)
            .GetMethod("FixupRawMftdataAt", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void InvokeFixupAt(System.IO.Filesystem.Ntfs.NtfsReader reader, byte[] buf, ulong offset, ulong len)
        => s_fixupAt.Invoke(reader, [buf, offset, len]);

    /// <summary>
    /// Builds a minimal 1024-byte FILE record buffer with valid USA fixup entries.
    /// USN stored at offset 48 (RecordHeader.UsaOffset); end-of-sector stamps at
    /// bytes 510 and 1022; USA replacements at offsets 50 and 52.
    /// </summary>
    private static byte[] BuildValidFileRecord(
        ushort usn = 0xBEEF,
        ushort sector1Original = 0x1111,
        ushort sector2Original = 0x2222)
    {
        byte[] buf = new byte[1024];

        // RecordHeader.Type = "FILE" (little-endian 0x454C4946)
        buf[0] = 0x46; buf[1] = 0x49; buf[2] = 0x4C; buf[3] = 0x45;

        // RecordHeader.UsaOffset = 48
        buf[4] = 48; buf[5] = 0;

        // RecordHeader.UsaCount = 3  (1 USN entry + 2 sector replacement entries)
        buf[6] = 3; buf[7] = 0;

        // Update Sequence Array starts at offset 48:
        //   [48..49] = current USN
        //   [50..51] = original last-2-bytes for sector 0
        //   [52..53] = original last-2-bytes for sector 1
        WriteUInt16(buf, 48, usn);
        WriteUInt16(buf, 50, sector1Original);
        WriteUInt16(buf, 52, sector2Original);

        // Stamp the USN at the end of each 512-byte sector.
        WriteUInt16(buf, 510, usn);
        WriteUInt16(buf, 1022, usn);

        return buf;
    }

    private static void WriteUInt16(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)(value >> 8);
    }

    private static ushort ReadUInt16(byte[] buf, int offset)
        => (ushort)(buf[offset] | (buf[offset + 1] << 8));

    [TestMethod]
    public void FixupRawMftdataAt_ReplacesSequenceNumbers_WithOriginals()
    {
        var reader = CreateBare();
        byte[] buf = BuildValidFileRecord(usn: 0xBEEF, sector1Original: 0x1111, sector2Original: 0x2222);

        InvokeFixupAt(reader, buf, 0UL, 1024UL);

        Assert.AreEqual((ushort)0x1111, ReadUInt16(buf, 510));
        Assert.AreEqual((ushort)0x2222, ReadUInt16(buf, 1022));
    }

    [TestMethod]
    public void FixupRawMftdataAt_NonFileRecord_IsNoOp()
    {
        var reader = CreateBare();
        byte[] buf = new byte[1024]; // all-zero → RecordType != FILE → no-op
        buf[510] = 0xAA; buf[511] = 0xBB;

        InvokeFixupAt(reader, buf, 0UL, 1024UL);

        // Sentinel bytes must not have been touched.
        Assert.AreEqual((byte)0xAA, buf[510]);
        Assert.AreEqual((byte)0xBB, buf[511]);
    }

    [TestMethod]
    public void FixupRawMftdataAt_CorruptUSN_ThrowsException()
    {
        var reader = CreateBare();
        byte[] buf = BuildValidFileRecord(usn: 0xBEEF);

        // Corrupt the end-of-sector stamp so it no longer matches the header USN.
        WriteUInt16(buf, 510, 0xDEAD);

        Assert.ThrowsException<TargetInvocationException>(() =>
            InvokeFixupAt(reader, buf, 0UL, 1024UL));
    }

    [TestMethod]
    public void FixupRawMftdataAt_AppliedAtNonZeroOffset()
    {
        var reader = CreateBare();

        // Embed the record at byte offset 1024 within a 2048-byte buffer.
        byte[] buf = new byte[2048];
        byte[] record = BuildValidFileRecord(usn: 0xCAFE, sector1Original: 0xAAAA, sector2Original: 0xBBBB);
        Array.Copy(record, 0, buf, 1024, 1024);

        InvokeFixupAt(reader, buf, 1024UL, 1024UL);

        Assert.AreEqual((ushort)0xAAAA, ReadUInt16(buf, 1024 + 510));
        Assert.AreEqual((ushort)0xBBBB, ReadUInt16(buf, 1024 + 1022));
    }
}
