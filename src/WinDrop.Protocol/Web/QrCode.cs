using System.Text;

namespace WinDrop.Protocol.Web;

/// <summary>
/// A QR code encoder (ISO/IEC 18004), written for one job: putting the phone page's
/// address on screen so an iPhone's camera can open it.
///
/// Deliberately narrow. Byte mode only, error correction level M only, versions 1 to 10.
/// The phone page's address is about 60 bytes, which fits version 4; version 10 holds 213.
/// Level M survives about 15% damage, which is plenty for a code on a screen, and every
/// table below covers just those ten versions at that one level, small enough to check by
/// hand against the standard.
///
/// The structure, in the order it is built: the data as a bit stream (mode, length,
/// bytes, terminator, padding), Reed-Solomon error correction per block, the blocks
/// interleaved, then the codewords laid out in a zigzag around the fixed patterns, and one
/// of eight masks XORed over the data so the result has no large blank areas or
/// finder-like shapes to confuse a scanner.
///
/// Coordinates are (x, y) = (column, row) internally, as the standard draws them.
/// </summary>
public sealed class QrCode
{
    public const int MaxVersion = 10;

    // Level M, by version. Index 0 is unused so a version indexes its own entry. Checked
    // against the standard's capacity table: data codewords = total - blocks * ecc, giving
    // 16, 28, 44, 64, 86, 108, 124, 154, 182, 216 for versions 1 to 10.
    private static readonly int[] EccCodewordsPerBlock = [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26];
    private static readonly int[] EccBlocks = [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5];

    /// <summary>The two format bits for level M. (L is 01, M 00, Q 11, H 10.)</summary>
    private const int LevelMFormatBits = 0b00;

    private readonly bool[,] _modules;    // [y, x], true = dark
    private readonly bool[,] _isFunction; // [y, x], true = fixed pattern, never masked

    public int Version { get; }

    /// <summary>Modules per side, without the quiet zone a scanner also needs around it.</summary>
    public int Size { get; }

    /// <summary>The mask chosen, 0 to 7.</summary>
    public int Mask { get; }

    /// <summary>True for a dark module.</summary>
    public bool this[int row, int column] => _modules[row, column];

    public static QrCode Encode(string text) => Encode(Encoding.UTF8.GetBytes(text));

    /// <summary>Encodes in the smallest version that fits.</summary>
    public static QrCode Encode(ReadOnlySpan<byte> data)
    {
        for (int version = 1; version <= MaxVersion; version++)
        {
            if (4 + CountBits(version) + data.Length * 8 <= DataCodewords(version) * 8)
                return new QrCode(version, DataCodewordsFor(data, version), forcedMask: null);
        }

        throw new ArgumentException($"{data.Length} bytes do not fit a version {MaxVersion} QR code.", nameof(data));
    }

    /// <summary>For tests that need a particular mask; <see cref="Encode(ReadOnlySpan{byte})"/> picks the best.</summary>
    internal static QrCode Encode(ReadOnlySpan<byte> data, int version, int mask) =>
        new(version, DataCodewordsFor(data, version), mask);

    private QrCode(int version, byte[] dataCodewords, int? forcedMask)
    {
        Version = version;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];

        DrawFunctionPatterns();
        DrawCodewords(AddEccAndInterleave(dataCodewords, version));

        Mask = forcedMask ?? ChooseMask();
        ApplyMask(Mask);
        DrawFormatBits(Mask);
    }

    // ---- the data ----------------------------------------------------------

    /// <summary>Byte mode's length field: 8 bits up to version 9, 16 from version 10.</summary>
    private static int CountBits(int version) => version <= 9 ? 8 : 16;

    private static int DataCodewords(int version) =>
        RawDataModules(version) / 8 - EccCodewordsPerBlock[version] * EccBlocks[version];

    /// <summary>
    /// Modules left for codewords once the fixed patterns are drawn: the whole square, less
    /// the finders with their separators and format areas, the timing lines, the alignment
    /// patterns, and from version 7 the two version blocks. Includes the remainder bits that
    /// do not make a whole codeword.
    /// </summary>
    internal static int RawDataModules(int version)
    {
        int result = (16 * version + 128) * version + 64;

        if (version >= 2)
        {
            int alignments = version / 7 + 2;
            result -= (25 * alignments - 10) * alignments - 55;
            if (version >= 7) result -= 36;
        }

        return result;
    }

    /// <summary>Mode, length, the bytes, a terminator, and the standard's alternating pad bytes.</summary>
    internal static byte[] DataCodewordsFor(ReadOnlySpan<byte> data, int version)
    {
        int capacityBits = DataCodewords(version) * 8;
        var bits = new BitBuffer();

        bits.Append(0b0100, 4); // byte mode
        bits.Append(data.Length, CountBits(version));
        foreach (byte b in data) bits.Append(b, 8);

        if (bits.Length > capacityBits)
            throw new ArgumentException($"{data.Length} bytes do not fit version {version}.", nameof(data));

        bits.Append(0, Math.Min(4, capacityBits - bits.Length));
        bits.Append(0, (8 - bits.Length % 8) % 8);

        for (int pad = 0xEC; bits.Length < capacityBits; pad ^= 0xEC ^ 0x11)
            bits.Append(pad, 8);

        return bits.ToBytes();
    }

    /// <summary>
    /// Splits the data into the version's blocks, appends each block's error correction,
    /// and interleaves: the first codeword of every block, then the second, and so on, data
    /// first and error correction after. Where blocks differ in length the short ones come
    /// first and are one data codeword shorter, and the interleaving skips the gap.
    /// </summary>
    internal static byte[] AddEccAndInterleave(byte[] data, int version)
    {
        int blockCount = EccBlocks[version];
        int eccLength = EccCodewordsPerBlock[version];
        int rawCodewords = RawDataModules(version) / 8;
        int shortBlocks = blockCount - rawCodewords % blockCount;
        int shortBlockLength = rawCodewords / blockCount;

        byte[] divisor = ReedSolomonDivisor(eccLength);
        var blocks = new byte[blockCount][];

        for (int i = 0, offset = 0; i < blockCount; i++)
        {
            int dataLength = shortBlockLength - eccLength + (i < shortBlocks ? 0 : 1);
            byte[] blockData = data[offset..(offset + dataLength)];
            offset += dataLength;

            var block = new byte[shortBlockLength + 1];
            blockData.CopyTo(block, 0);
            ReedSolomonRemainder(blockData, divisor).CopyTo(block, block.Length - eccLength);
            blocks[i] = block;
        }

        var result = new byte[rawCodewords];

        for (int i = 0, k = 0; i < blocks[0].Length; i++)
        {
            for (int j = 0; j < blockCount; j++)
            {
                // A short block's slot at its missing data codeword is padding, not data.
                if (i != shortBlockLength - eccLength || j >= shortBlocks)
                    result[k++] = blocks[j][i];
            }
        }

        return result;
    }

    // ---- Reed-Solomon over GF(2^8), polynomial x^8 + x^4 + x^3 + x^2 + 1 ------

    /// <summary>The generator polynomial (x - a^0)(x - a^1)...(x - a^(degree-1)), leading 1 dropped.</summary>
    internal static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;

        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = (byte)GfMultiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }

            root = GfMultiply(root, 0x02);
        }

        return result;
    }

    /// <summary>The error correction codewords: the remainder of data * x^degree divided by the generator.</summary>
    internal static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];

        foreach (byte b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;

            for (int i = 0; i < result.Length; i++)
                result[i] ^= (byte)GfMultiply(divisor[i], factor);
        }

        return result;
    }

    private static int GfMultiply(int x, int y)
    {
        int z = 0;

        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }

        return z;
    }

    // ---- the fixed patterns ------------------------------------------------

    private void DrawFunctionPatterns()
    {
        // Timing lines along row 6 and column 6, dark on even positions. The finders drawn
        // next overwrite their ends.
        for (int i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }

        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        int[] positions = AlignmentPositions(Version);

        for (int i = 0; i < positions.Length; i++)
        {
            for (int j = 0; j < positions.Length; j++)
            {
                // Not where a finder already sits: three of the grid's corners.
                bool finderCorner = (i == 0 && j == 0)
                    || (i == 0 && j == positions.Length - 1)
                    || (i == positions.Length - 1 && j == 0);

                if (!finderCorner) DrawAlignment(positions[i], positions[j]);
            }
        }

        // Reserved now so the codewords flow around them; the real bits go in after masking.
        DrawFormatBits(0);
        DrawVersion();
    }

    /// <summary>
    /// The centres of the alignment patterns along each axis: 6, then evenly spaced up to
    /// Size - 7, with the spacing rounded to an even number as the standard's table does.
    /// Version 1 has none.
    /// </summary>
    internal static int[] AlignmentPositions(int version)
    {
        if (version == 1) return [];

        int count = version / 7 + 2;
        int size = version * 4 + 17;
        int step = (version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;

        var result = new int[count];
        result[0] = 6;

        for (int i = count - 1, position = size - 7; i >= 1; i--, position -= step)
            result[i] = position;

        return result;
    }

    /// <summary>A 7x7 finder centred on (x, y), with its light separator ring around it.</summary>
    private void DrawFinder(int x, int y)
    {
        for (int dy = -4; dy <= 4; dy++)
        {
            for (int dx = -4; dx <= 4; dx++)
            {
                int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int xx = x + dx, yy = y + dy;

                if (xx >= 0 && xx < Size && yy >= 0 && yy < Size)
                    SetFunction(xx, yy, distance != 2 && distance != 4);
            }
        }
    }

    private void DrawAlignment(int x, int y)
    {
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
                SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }
    }

    /// <summary>
    /// The 15 format bits: level and mask, 10 bits of BCH error correction, then XORed with
    /// 101010000010010 so they are never all light. Written twice, once around the top-left
    /// finder and once split between the other two, plus the one module that is always dark.
    /// </summary>
    private void DrawFormatBits(int mask)
    {
        int bits = FormatBits(mask);

        for (int i = 0; i <= 5; i++) SetFunction(8, i, Bit(bits, i));
        SetFunction(8, 7, Bit(bits, 6));
        SetFunction(8, 8, Bit(bits, 7));
        SetFunction(7, 8, Bit(bits, 8));
        for (int i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(bits, i));

        for (int i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(bits, i));
        for (int i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(bits, i));
        SetFunction(8, Size - 8, true);
    }

    internal static int FormatBits(int mask)
    {
        int data = LevelMFormatBits << 3 | mask;
        int remainder = data;

        for (int i = 0; i < 10; i++)
            remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);

        return (data << 10 | remainder) ^ 0x5412;
    }

    /// <summary>From version 7: the version number and 12 bits of BCH, in two 6x3 blocks.</summary>
    private void DrawVersion()
    {
        if (Version < 7) return;

        int bits = VersionBits(Version);

        for (int i = 0; i < 18; i++)
        {
            bool bit = Bit(bits, i);
            int a = Size - 11 + i % 3;
            int b = i / 3;

            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    internal static int VersionBits(int version)
    {
        int remainder = version;

        for (int i = 0; i < 12; i++)
            remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);

        return version << 12 | remainder;
    }

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _isFunction[y, x] = true;
    }

    // ---- the codewords -----------------------------------------------------

    /// <summary>
    /// Two columns at a time from the right edge, up then down then up, skipping the
    /// vertical timing line and every fixed module. Most significant bit first.
    /// </summary>
    private void DrawCodewords(byte[] codewords)
    {
        int i = 0;

        for (int right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;

            for (int vertical = 0; vertical < Size; vertical++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? Size - 1 - vertical : vertical;

                    if (!_isFunction[y, x] && i < codewords.Length * 8)
                    {
                        _modules[y, x] = Bit(codewords[i >> 3], 7 - (i & 7));
                        i++;
                    }

                    // Modules past the last codeword are the remainder bits, left light.
                }
            }
        }
    }

    // ---- masking -----------------------------------------------------------

    /// <summary>XORs a mask over every non-fixed module. Applying it twice undoes it.</summary>
    private void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                if (!_isFunction[y, x] && MaskCondition(mask, x, y))
                    _modules[y, x] = !_modules[y, x];
            }
        }
    }

    internal static bool MaskCondition(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => (x / 3 + y / 2) % 2 == 0,
        5 => x * y % 2 + x * y % 3 == 0,
        6 => (x * y % 2 + x * y % 3) % 2 == 0,
        7 => ((x + y) % 2 + x * y % 3) % 2 == 0,
        _ => throw new ArgumentOutOfRangeException(nameof(mask)),
    };

    /// <summary>
    /// Tries all eight masks and keeps the lowest penalty. Every mask gives a valid code;
    /// the penalty only prefers the one a scanner will find easiest.
    /// </summary>
    private int ChooseMask()
    {
        int best = 0;
        int bestPenalty = int.MaxValue;

        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(mask);

            int penalty = Penalty();
            if (penalty < bestPenalty)
            {
                best = mask;
                bestPenalty = penalty;
            }

            ApplyMask(mask);
        }

        return best;
    }

    /// <summary>
    /// The standard's four rules: runs of five or more alike in a line, 2x2 blocks alike,
    /// anything that looks like a finder (1:1:3:1:1 with four light beside it), and a
    /// dark proportion far from half.
    /// </summary>
    private int Penalty()
    {
        int penalty = 0;

        for (int line = 0; line < Size; line++)
        {
            penalty += RunPenalty(i => _modules[line, i]);
            penalty += RunPenalty(i => _modules[i, line]);
            penalty += FinderLikePenalty(i => _modules[line, i]);
            penalty += FinderLikePenalty(i => _modules[i, line]);
        }

        for (int y = 0; y < Size - 1; y++)
        {
            for (int x = 0; x < Size - 1; x++)
            {
                bool c = _modules[y, x];
                if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1])
                    penalty += 3;
            }
        }

        int dark = 0;
        foreach (bool module in _modules)
            if (module) dark++;

        int total = Size * Size;
        int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        penalty += Math.Max(0, k) * 10;

        return penalty;
    }

    private int RunPenalty(Func<int, bool> at)
    {
        int penalty = 0;
        int run = 1;

        for (int i = 1; i < Size; i++)
        {
            if (at(i) == at(i - 1))
            {
                run++;
                continue;
            }

            if (run >= 5) penalty += 3 + (run - 5);
            run = 1;
        }

        if (run >= 5) penalty += 3 + (run - 5);
        return penalty;
    }

    private static readonly bool[] FinderLikeA = [true, false, true, true, true, false, true, false, false, false, false];
    private static readonly bool[] FinderLikeB = [false, false, false, false, true, false, true, true, true, false, true];

    private int FinderLikePenalty(Func<int, bool> at)
    {
        int penalty = 0;

        for (int start = 0; start + 11 <= Size; start++)
        {
            if (Matches(FinderLikeA)) penalty += 40;
            if (Matches(FinderLikeB)) penalty += 40;

            bool Matches(bool[] pattern)
            {
                for (int i = 0; i < pattern.Length; i++)
                    if (at(start + i) != pattern[i]) return false;
                return true;
            }
        }

        return penalty;
    }

    private static bool Bit(int value, int index) => ((value >> index) & 1) != 0;

    private sealed class BitBuffer
    {
        private readonly List<bool> _bits = [];

        public int Length => _bits.Count;

        public void Append(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
                _bits.Add(((value >> i) & 1) != 0);
        }

        public byte[] ToBytes()
        {
            var result = new byte[_bits.Count / 8];

            for (int i = 0; i < _bits.Count; i++)
                if (_bits[i]) result[i / 8] |= (byte)(0x80 >> (i % 8));

            return result;
        }
    }
}
