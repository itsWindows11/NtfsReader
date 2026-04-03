using System.IO.Filesystem.Ntfs;

namespace NtfsReader.Tests;

/// <summary>
/// Unit tests for the NTFS run-list (data-run) decoder.
///
/// NTFS stores the physical location of non-resident attribute data as a variable-length
/// encoded list of (length, offset) pairs called a "run list". The decoder must handle:
///   - Single-byte and multi-byte length/offset values (little-endian)
///   - Negative (sign-extended) relative offsets
///   - Sparse (virtual) runs where the offset size nibble is 0
///   - Proper index advancement after each field is consumed
///
/// These tests call the <c>internal</c> wrappers <c>NtfsReader.DecodeRunLength</c> and
/// <c>NtfsReader.DecodeRunOffset</c> which delegate to the private unsafe implementations.
/// </summary>
public class RunListDecoderTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // DecodeRunLength tests
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RunLength_OneByte_ReturnsCorrectValue()
    {
        // A single-byte run length of 42 should decode to 42.
        byte[] data = [42, 0xFF /*padding to satisfy length check*/];
        uint index = 0;
        long result = System.IO.Filesystem.Ntfs.NtfsReader.DecodeRunLength(data, 1, ref index);
        Assert.Equal(42L, result);
        Assert.Equal(1u, index); // exactly 1 byte consumed
    }

    [Fact]
    public void RunLength_TwoBytes_LittleEndian()
    {
        // 0x01, 0x02 little-endian → 0x0201 = 513
        byte[] data = [0x01, 0x02, 0xFF];
        uint index = 0;
        long result = System.IO.Filesystem.Ntfs.NtfsReader.DecodeRunLength(data, 2, ref index);
        Assert.Equal(0x0201L, result);
        Assert.Equal(2u, index);
    }

    [Fact]
    public void RunLength_FourBytes_LittleEndian()
    {
        // 0x78,0x56,0x34,0x12 little-endian → 0x12345678
        byte[] data = [0x78, 0x56, 0x34, 0x12, 0xFF];
        uint index = 0;
        long result = System.IO.Filesystem.Ntfs.NtfsReader.DecodeRunLength(data, 4, ref index);
        Assert.Equal(0x12345678L, result);
        Assert.Equal(4u, index);
    }

    [Fact]
    public void RunLength_ZeroSize_ReturnsZeroAndConsumesNoBytes()
    {
        // runLengthSize == 0 signals a sparse (virtual) run: no bytes touched, result 0.
        byte[] data = [0xFF, 0xFF];
        uint index = 0;
        long result = System.IO.Filesystem.Ntfs.NtfsReader.DecodeRunLength(data, 0, ref index);
        Assert.Equal(0L, result);
        Assert.Equal(0u, index);
    }

    [Fact]
    public void RunLength_IndexAdvancesCorrectlyForMultipleReads()
    {
        // Simulate two back-to-back run lengths in the same buffer.
        // First: 2-byte 0x0100 = 256; Second: 1-byte 0x0A = 10.
        byte[] data = [0x00, 0x01, 0x0A, 0xFF];
        uint index = 0;
        long first = System.IO.Filesystem.Ntfs.NtfsReader.DecodeRunLength(data, 2, ref index);
        long second = System.IO.Filesystem.Ntfs.NtfsReader.DecodeRunLength(data, 1, ref index);
        Assert.Equal(256L, first);
        Assert.Equal(10L, second);
        Assert.Equal(3u, index);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // DecodeRunOffset tests
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RunOffset_PositiveOneByte()
    {
        byte[] data = [0x05, 0xFF];
        uint index = 0;
        long result = System.IO.Filesystem.Ntfs.NtfsReader.DecodeRunOffset(data, 1, ref index);
        Assert.Equal(5L, result);
        Assert.Equal(1u, index);
    }

    [Fact]
    public void RunOffset_NegativeSignExtended_OneByte()
    {
        // 0xFF = -1 when sign-extended to 64 bits (high bit set → negative).
        byte[] data = [0xFF, 0x00];
        uint index = 0;
        long result = System.IO.Filesystem.Ntfs.NtfsReader.DecodeRunOffset(data, 1, ref index);
        Assert.Equal(-1L, result);
    }

    [Fact]
    public void RunOffset_NegativeSignExtended_TwoBytes()
    {
        // 0xFE,0xFF little-endian 0xFFFE, sign-extended → -2
        byte[] data = [0xFE, 0xFF, 0x00];
        uint index = 0;
        long result = System.IO.Filesystem.Ntfs.NtfsReader.DecodeRunOffset(data, 2, ref index);
        Assert.Equal(-2L, result);
    }

    [Fact]
    public void RunOffset_PositiveTwoBytes_NoSignExtension()
    {
        // 0x00,0x40 little-endian → 0x4000 = 16384 (high bit of byte[1] is 0 → positive).
        byte[] data = [0x00, 0x40, 0xFF];
        uint index = 0;
        long result = System.IO.Filesystem.Ntfs.NtfsReader.DecodeRunOffset(data, 2, ref index);
        Assert.Equal(0x4000L, result);
    }

    [Fact]
    public void RunOffset_ZeroSize_ReturnsZeroAndConsumesNoBytes()
    {
        // runOffsetSize == 0 means sparse run; LCN delta = 0, nothing consumed.
        byte[] data = [0xFF, 0xFF];
        uint index = 0;
        long result = System.IO.Filesystem.Ntfs.NtfsReader.DecodeRunOffset(data, 0, ref index);
        Assert.Equal(0L, result);
        Assert.Equal(0u, index);
    }

    [Fact]
    public void RunOffset_LargeDiskPosition_SevenBytes()
    {
        // Real-world: 7-byte LCN delta for a disk with a large MFT offset.
        // 0x01,0x00,0x00,0x00,0x00,0x00,0x01 → 0x0001_0000_0000_0001
        byte[] data = [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00];
        uint index = 0;
        long result = System.IO.Filesystem.Ntfs.NtfsReader.DecodeRunOffset(data, 7, ref index);
        Assert.Equal(0x00_01_00_00_00_00_00_01L, result);
        Assert.Equal(7u, index);
    }
}
