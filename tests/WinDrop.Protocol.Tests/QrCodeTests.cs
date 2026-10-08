using System.Text;
using WinDrop.Protocol.Web;

namespace WinDrop.Protocol.Tests;

/// <summary>
/// The QR encoder, checked against values published with the standard rather than
/// against itself: Reed-Solomon codewords, format and version bits, and the capacity and
/// alignment tables. Then a whole code is read back by <see cref="QrReader"/>, which
/// locates the fixed patterns its own way, so a placement or interleaving mistake shows
/// as a code that does not decode. The final check is an iPhone camera; see
/// docs/phone-page.md.
/// </summary>
public class QrCodeTests
{
    [Fact]
    public void ReedSolomonMatchesThePublishedHelloWorldExample()
    {
        // The worked 1-M example from the standard's tutorials ("HELLO WORLD").
        byte[] data = [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17];
        byte[] expected = [196, 35, 39, 119, 235, 215, 231, 226, 93, 23];

        Assert.Equal(expected, QrCode.ReedSolomonRemainder(data, QrCode.ReedSolomonDivisor(10)));
    }

    [Theory]
    [InlineData(0, "101010000010010")]
    [InlineData(1, "101000100100101")]
    [InlineData(2, "101111001111100")]
    [InlineData(3, "101101101001011")]
    [InlineData(4, "100010111111001")]
    [InlineData(5, "100000011001110")]
    [InlineData(6, "100111110010111")]
    [InlineData(7, "100101010100000")]
    public void FormatBitsMatchTheStandardsTableForLevelM(int mask, string expected)
    {
        Assert.Equal(expected, Convert.ToString(QrCode.FormatBits(mask), 2).PadLeft(15, '0'));
    }

    [Theory]
    [InlineData(7, 0x07C94)]
    [InlineData(8, 0x085BC)]
    [InlineData(9, 0x09A99)]
    [InlineData(10, 0x0A4D3)]
    public void VersionBitsMatchTheStandardsTable(int version, int expected)
    {
        Assert.Equal(expected, QrCode.VersionBits(version));
    }

    [Theory]
    [InlineData(1, new int[0])]
    [InlineData(2, new[] { 6, 18 })]
    [InlineData(3, new[] { 6, 22 })]
    [InlineData(4, new[] { 6, 26 })]
    [InlineData(5, new[] { 6, 30 })]
    [InlineData(6, new[] { 6, 34 })]
    [InlineData(7, new[] { 6, 22, 38 })]
    [InlineData(8, new[] { 6, 24, 42 })]
    [InlineData(9, new[] { 6, 26, 46 })]
    [InlineData(10, new[] { 6, 28, 50 })]
    public void AlignmentPositionsMatchTheStandardsTable(int version, int[] expected)
    {
        Assert.Equal(expected, QrCode.AlignmentPositions(version));
    }

    [Theory]
    [InlineData(1, 26)]
    [InlineData(2, 44)]
    [InlineData(3, 70)]
    [InlineData(4, 100)]
    [InlineData(5, 134)]
    [InlineData(6, 172)]
    [InlineData(7, 196)]
    [InlineData(8, 242)]
    [InlineData(9, 292)]
    [InlineData(10, 346)]
    public void TotalCodewordsMatchTheStandardsTable(int version, int expected)
    {
        Assert.Equal(expected, QrCode.RawDataModules(version) / 8);
    }

    [Fact]
    public void ByteModeDataIsModeLengthBytesTerminatorThenAlternatingPads()
    {
        // 0100 | 00000010 | 01101000 01101001 | 0000, then EC 11 EC 11 ... to 16 bytes.
        byte[] codewords = QrCode.DataCodewordsFor("hi"u8, version: 1);

        Assert.Equal(
            [0x40, 0x26, 0x86, 0x90, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11],
            codewords);
    }

    [Theory]
    [InlineData(14, 1)]  // version 1-M holds 14 bytes
    [InlineData(15, 2)]
    [InlineData(62, 4)]  // a phone page address is about this long
    [InlineData(63, 5)]
    [InlineData(213, 10)]
    public void PicksTheSmallestVersionThatFits(int length, int expectedVersion)
    {
        Assert.Equal(expectedVersion, QrCode.Encode(new byte[length]).Version);
    }

    [Fact]
    public void RefusesDataBeyondVersionTen()
    {
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new byte[214]));
    }

    [Fact]
    public void FindersSitInThreeCornersWithTheirSeparators()
    {
        QrCode code = QrCode.Encode("http://192.168.1.20:8771/abcdefghijklmnopqrstuv/");

        foreach ((int top, int left) in new[] { (0, 0), (0, code.Size - 7), (code.Size - 7, 0) })
        {
            for (int r = 0; r < 7; r++)
            {
                for (int c = 0; c < 7; c++)
                {
                    int ring = Math.Max(Math.Abs(r - 3), Math.Abs(c - 3));
                    Assert.Equal(ring != 2, code[top + r, left + c]);
                }
            }
        }

        // The separator: one light module around each finder, on the sides facing the code.
        for (int i = 0; i < 8; i++)
        {
            Assert.False(code[7, i]);
            Assert.False(code[i, 7]);
            Assert.False(code[7, code.Size - 1 - i]);
            Assert.False(code[code.Size - 8, i]);
        }
    }

    [Fact]
    public void TimingLinesAlternateAndTheDarkModuleIsDark()
    {
        QrCode code = QrCode.Encode("timing");

        for (int i = 8; i < code.Size - 8; i++)
        {
            Assert.Equal(i % 2 == 0, code[6, i]);
            Assert.Equal(i % 2 == 0, code[i, 6]);
        }

        Assert.True(code[code.Size - 8, 8]);
    }

    [Theory]
    [InlineData("hi")]
    [InlineData("http://192.0.2.10:8771/AbCdEfGhIjKlMnOpQrStUv/")]
    [InlineData("http://[fe80::1]:8771/abcdefghijklmnopqrstuv/")]
    public void ReadsBackUnderEveryMask(string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        int version = QrCode.Encode(data).Version;

        for (int mask = 0; mask < 8; mask++)
        {
            QrCode code = QrCode.Encode(data, version, mask);
            Assert.Equal(text, QrReader.Read(code));
        }
    }

    [Fact]
    public void ReadsBackAcrossEveryVersion()
    {
        // Versions 7 to 10 add the version blocks and a third row of alignment patterns,
        // and 8 to 10 mix block lengths, so each is worth reading back.
        for (int version = 1; version <= QrCode.MaxVersion; version++)
        {
            int capacity = version switch { 1 => 14, 2 => 26, 3 => 42, 4 => 62, 5 => 84, 6 => 106, 7 => 122, 8 => 152, 9 => 180, _ => 213 };
            byte[] data = Enumerable.Range(0, capacity).Select(i => (byte)(i * 37 + version)).ToArray();

            QrCode code = QrCode.Encode(data);
            Assert.Equal(version, code.Version);
            Assert.Equal(data, QrReader.ReadBytes(code));
        }
    }

    [Fact]
    public void TheFormatBitsCarryTheMaskThatWasApplied()
    {
        QrCode code = QrCode.Encode("which mask");
        Assert.Equal(code.Mask, QrReader.MaskFromFormatBits(code));
    }

    [Fact]
    public void BothCopiesOfTheFormatBitsAgree()
    {
        // A scanner may read either copy, depending on which corner it sees clearly.
        for (int mask = 0; mask < 8; mask++)
        {
            QrCode code = QrCode.Encode("two copies"u8, version: 2, mask);
            Assert.Equal(QrReader.FirstFormatCopy(code), QrReader.SecondFormatCopy(code));
            Assert.Equal(QrCode.FormatBits(mask), QrReader.FirstFormatCopy(code));
        }
    }
}

/// <summary>
/// Reads a QR code back, for tests. Written from the standard's description of the layout
/// rather than by calling into the encoder's drawing code: the fixed areas are marked by
/// their documented sizes, so if the encoder put a codeword where a fixed pattern belongs,
/// or interleaved the blocks wrongly, the bytes read here come out wrong.
///
/// Level M and byte mode only, like the encoder. It uses the encoder's Reed-Solomon to
/// check each block, which the published vector above pins down independently.
/// </summary>
internal static class QrReader
{
    // Level M, versions 1 to 10: (error correction per block, blocks).
    private static readonly (int Ecc, int Blocks)[] Layout =
        [(0, 0), (10, 1), (16, 1), (26, 1), (18, 2), (24, 2), (16, 4), (18, 4), (22, 4), (22, 5), (26, 5)];

    public static string Read(QrCode code) => Encoding.UTF8.GetString(ReadBytes(code));

    public static int MaskFromFormatBits(QrCode code)
    {
        // The first copy, read as the standard lists it: row 8 from the left (skipping the
        // timing column), then column 8 upwards (skipping the timing row). Bit 14 first.
        int bits = FirstFormatCopy(code) ^ 0x5412;
        Assert.Equal(0, bits >> 13); // level M is 00
        return (bits >> 10) & 7;
    }

    public static byte[] ReadBytes(QrCode code)
    {
        int size = code.Size;
        int version = (size - 17) / 4;
        bool[,] fixedArea = FixedAreas(version, size);
        int mask = MaskFromFormatBits(code);

        // The zigzag: column pairs from the right, skipping column 6, alternating direction.
        var bits = new List<bool>();
        bool upward = true;

        for (int right = size - 1; right > 0; right -= 2)
        {
            if (right == 6) right--;

            for (int step = 0; step < size; step++)
            {
                int row = upward ? size - 1 - step : step;

                for (int column = right; column >= right - 1; column--)
                {
                    if (fixedArea[row, column]) continue;
                    bits.Add(code[row, column] ^ Masked(mask, row, column));
                }
            }

            upward = !upward;
        }

        var codewords = new byte[bits.Count / 8];
        for (int i = 0; i < codewords.Length * 8; i++)
            if (bits[i]) codewords[i / 8] |= (byte)(0x80 >> (i % 8));

        byte[] data = Deinterleave(codewords, version);

        // Byte mode, then the length, then the bytes.
        int bitIndex = 0;
        int Take(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++, bitIndex++)
                value = value << 1 | ((data[bitIndex / 8] >> (7 - bitIndex % 8)) & 1);
            return value;
        }

        Assert.Equal(0b0100, Take(4));
        int length = Take(version <= 9 ? 8 : 16);
        return Enumerable.Range(0, length).Select(_ => (byte)Take(8)).ToArray();
    }

    /// <summary>
    /// The eight masks as the standard writes them, with i the row and j the column, kept
    /// apart from the encoder's own (x, y) version so a swapped axis cannot cancel out.
    /// </summary>
    private static bool Masked(int mask, int i, int j) => mask switch
    {
        0 => (i + j) % 2 == 0,
        1 => i % 2 == 0,
        2 => j % 3 == 0,
        3 => (i + j) % 3 == 0,
        4 => (i / 2 + j / 3) % 2 == 0,
        5 => i * j % 2 + i * j % 3 == 0,
        6 => (i * j % 2 + i * j % 3) % 2 == 0,
        _ => ((i + j) % 2 + i * j % 3) % 2 == 0,
    };

    /// <summary>The second copy of the format bits: column 8 from the bottom up, then row 8 rightwards.</summary>
    public static int SecondFormatCopy(QrCode code)
    {
        int size = code.Size;
        int bits = 0;

        for (int r = size - 1; r >= size - 7; r--) bits = bits << 1 | (code[r, 8] ? 1 : 0);
        for (int c = size - 8; c < size; c++) bits = bits << 1 | (code[8, c] ? 1 : 0);

        return bits;
    }

    public static int FirstFormatCopy(QrCode code)
    {
        int bits = 0;

        for (int c = 0; c <= 8; c++) if (c != 6) bits = bits << 1 | (code[8, c] ? 1 : 0);
        for (int r = 7; r >= 0; r--) if (r != 6) bits = bits << 1 | (code[r, 8] ? 1 : 0);

        return bits;
    }

    private static byte[] Deinterleave(byte[] codewords, int version)
    {
        (int ecc, int blockCount) = Layout[version];
        int total = codewords.Length;
        int dataTotal = total - ecc * blockCount;
        int shortData = dataTotal / blockCount;
        int longBlocks = dataTotal % blockCount;
        int shortBlocks = blockCount - longBlocks;

        var blocks = Enumerable.Range(0, blockCount)
            .Select(b => new List<byte>())
            .ToArray();

        int k = 0;

        // Data codewords: one from each block in turn; only the long blocks have the last.
        for (int i = 0; i <= shortData; i++)
        {
            for (int b = 0; b < blockCount; b++)
            {
                if (i == shortData && b < shortBlocks) continue;
                blocks[b].Add(codewords[k++]);
            }
        }

        var eccBlocks = Enumerable.Range(0, blockCount).Select(_ => new List<byte>()).ToArray();
        for (int i = 0; i < ecc; i++)
            for (int b = 0; b < blockCount; b++)
                eccBlocks[b].Add(codewords[k++]);

        Assert.Equal(total, k);

        byte[] divisor = QrCode.ReedSolomonDivisor(ecc);
        for (int b = 0; b < blockCount; b++)
            Assert.Equal(eccBlocks[b].ToArray(), QrCode.ReedSolomonRemainder(blocks[b].ToArray(), divisor));

        return blocks.SelectMany(b => b).ToArray();
    }

    /// <summary>Every module that is not data, by the standard's documented areas.</summary>
    private static bool[,] FixedAreas(int version, int size)
    {
        var area = new bool[size, size];

        void Mark(int top, int left, int height, int width)
        {
            for (int r = top; r < top + height; r++)
                for (int c = left; c < left + width; c++)
                    if (r >= 0 && c >= 0 && r < size && c < size) area[r, c] = true;
        }

        // Finder, separator and format area: 9x9 at the top left, 8 wide by 9 tall at the
        // top right, 9 wide by 8 tall at the bottom left (the dark module included).
        Mark(0, 0, 9, 9);
        Mark(0, size - 8, 9, 8);
        Mark(size - 8, 0, 8, 9);

        // Timing lines.
        Mark(6, 0, 1, size);
        Mark(0, 6, size, 1);

        // Alignment patterns, 5x5, except where they would overlap a finder.
        int[] centres = QrCode.AlignmentPositions(version);
        foreach (int r in centres)
        {
            foreach (int c in centres)
            {
                bool nearFinder = (r < 9 && c < 9) || (r < 9 && c > size - 9) || (r > size - 9 && c < 9);
                if (!nearFinder) Mark(r - 2, c - 2, 5, 5);
            }
        }

        // Version information from version 7: 6 wide by 3 tall above the bottom-left finder,
        // and 3 wide by 6 tall left of the top-right one.
        if (version >= 7)
        {
            Mark(0, size - 11, 6, 3);
            Mark(size - 11, 0, 3, 6);
        }

        return area;
    }
}
