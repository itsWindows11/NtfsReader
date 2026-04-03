using System.IO.Filesystem.Ntfs;

namespace NtfsReader.Tests;

/// <summary>
/// Unit tests for the resident-<c>$BITMAP</c> fallback path in <see cref="NtfsReader"/>.
///
/// On small or newly-formatted NTFS volumes the <c>$BITMAP</c> attribute of the
/// <c>$MFT</c> inode can fit entirely within the MFT record (i.e. it is <b>resident</b>).
/// In that case the bitmap data must be extracted directly from the raw MFT record
/// because there is no run-list for the fragment-walking code to follow.
///
/// The tests exercise the internal helper
/// <c>NtfsReader.TryExtractResidentBitmapDataAt</c> which the library exposes via
/// <c>InternalsVisibleTo</c>.
/// </summary>
[TestClass]
public class ResidentBitmapTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Fake MFT record builder
    // ─────────────────────────────────────────────────────────────────────────

    // FileRecordHeader layout (Pack=1):
    //   Offset 0  – RecordHeader (16 bytes)
    //   Offset 16 – SequenceNumber (2)
    //   Offset 18 – LinkCount (2)
    //   Offset 20 – AttributeOffset (2)  ← we write the attribute start here
    //   … (rest not needed by TryExtractResidentBitmapData)
    private const int AttributeOffsetField = 20; // byte position of the ushort

    // Attributes begin at this byte (50-byte header, rounded up to 8-byte boundary).
    private const ushort AttrsStart = 56;

    // ResidentAttribute layout (Pack=1, AttributeType.AttributeBitmap = 0xB0):
    //   Offset 0  – AttributeType (4)  → 0xB0 0x00 0x00 0x00
    //   Offset 4  – Length (4)         → 32 (24-byte header + data padded to 8)
    //   Offset 8  – Nonresident (1)    → 0x00 (resident)
    //   Offset 9  – NameLength (1)     → 0x00
    //   Offset 10 – NameOffset (2)     → 0x00 0x00
    //   Offset 12 – Flags (2)          → 0x00 0x00
    //   Offset 14 – AttributeNumber (2)→ 0x00 0x00
    //   Offset 16 – ValueLength (4)    → len of bitmap data
    //   Offset 20 – ValueOffset (2)    → 24 (byte offset to data within attribute)
    //   Offset 22 – Flags (2)          → 0x00 0x00
    //   Offset 24 – <bitmap data>

    private static byte[] BuildRecordWithResidentBitmap(byte[] bitmapData)
    {
        // Total attribute size must be aligned to 8 bytes.
        int dataLen = bitmapData.Length;
        int attrSize = ((24 + dataLen) + 7) & ~7; // round up to 8

        byte[] buf = new byte[1024]; // large enough; zeroed

        // Set AttributeOffset field in FileRecordHeader.
        buf[AttributeOffsetField]     = (byte)(AttrsStart & 0xFF);
        buf[AttributeOffsetField + 1] = (byte)(AttrsStart >> 8);

        int a = AttrsStart; // attribute starts here

        // AttributeType = AttributeBitmap = 0xB0
        buf[a + 0] = 0xB0; buf[a + 1] = 0x00; buf[a + 2] = 0x00; buf[a + 3] = 0x00;

        // Length
        buf[a + 4] = (byte)(attrSize & 0xFF);
        buf[a + 5] = (byte)((attrSize >> 8) & 0xFF);

        // Nonresident = 0 (resident) – already zero.

        // ValueLength (uint at attribute offset +16)
        buf[a + 16] = (byte)(dataLen & 0xFF);
        buf[a + 17] = (byte)((dataLen >> 8) & 0xFF);

        // ValueOffset (ushort at attribute offset +20) = 24 (header size)
        buf[a + 20] = 24;

        // Bitmap data starting at attribute offset +24
        for (int i = 0; i < dataLen; i++)
            buf[a + 24 + i] = bitmapData[i];

        // End marker at next attribute position
        int endMarkerOffset = a + attrSize;
        buf[endMarkerOffset]     = 0xFF;
        buf[endMarkerOffset + 1] = 0xFF;
        buf[endMarkerOffset + 2] = 0xFF;
        buf[endMarkerOffset + 3] = 0xFF;

        return buf;
    }

    private static byte[] BuildRecordWithNoAttributeOfType(bool addEndMarker)
    {
        byte[] buf = new byte[512];

        buf[AttributeOffsetField]     = (byte)(AttrsStart & 0xFF);
        buf[AttributeOffsetField + 1] = (byte)(AttrsStart >> 8);

        if (addEndMarker)
        {
            int e = AttrsStart;
            buf[e] = 0xFF; buf[e + 1] = 0xFF; buf[e + 2] = 0xFF; buf[e + 3] = 0xFF;
        }
        // Without an end marker the zeroed Length=0 guard kicks in → also returns null.

        return buf;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tests
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void TryExtractResidentBitmapDataAt_ReturnsBitmapBytes_WhenAttributeIsResident()
    {
        byte[] expected = [0xFF, 0x0F]; // first 12 inodes in use
        byte[] buf = BuildRecordWithResidentBitmap(expected);

        byte[] result = System.IO.Filesystem.Ntfs.NtfsReader.TryExtractResidentBitmapDataAt(buf, (ulong)buf.Length);

        Assert.IsNotNull(result, "Expected resident bitmap data to be extracted.");
        CollectionAssert.AreEqual(expected, result);
    }

    [TestMethod]
    public void TryExtractResidentBitmapDataAt_ReturnsSingleByte_WhenBitmapIsOneByte()
    {
        byte[] expected = [0b_0001_1111]; // inodes 0-4 in use
        byte[] buf = BuildRecordWithResidentBitmap(expected);

        byte[] result = System.IO.Filesystem.Ntfs.NtfsReader.TryExtractResidentBitmapDataAt(buf, (ulong)buf.Length);

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(expected, result);
    }

    [TestMethod]
    public void TryExtractResidentBitmapDataAt_ReturnsNull_WhenNoAttributesPresent()
    {
        // All-zero buffer except AttributeOffset → zeroed Length triggers the guard → null.
        byte[] buf = BuildRecordWithNoAttributeOfType(addEndMarker: false);

        byte[] result = System.IO.Filesystem.Ntfs.NtfsReader.TryExtractResidentBitmapDataAt(buf, (ulong)buf.Length);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void TryExtractResidentBitmapDataAt_ReturnsNull_WhenOnlyEndMarkerPresent()
    {
        byte[] buf = BuildRecordWithNoAttributeOfType(addEndMarker: true);

        byte[] result = System.IO.Filesystem.Ntfs.NtfsReader.TryExtractResidentBitmapDataAt(buf, (ulong)buf.Length);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void TryExtractResidentBitmapDataAt_ReturnsNull_WhenAttributeOffsetExceedsRecordLength()
    {
        byte[] buf = new byte[512];
        // Set AttributeOffset to beyond recordLen (we pass recordLen = 64).
        buf[AttributeOffsetField]     = 0x80; // 128 > 64
        buf[AttributeOffsetField + 1] = 0x00;

        byte[] result = System.IO.Filesystem.Ntfs.NtfsReader.TryExtractResidentBitmapDataAt(buf, 64);

        Assert.IsNull(result);
    }
}
