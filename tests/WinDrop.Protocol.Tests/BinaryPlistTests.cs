using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using WinDrop.Protocol.Plist;
using Xunit;

namespace WinDrop.Protocol.Tests;

/// <summary>
/// Fixtures produced by Python's plistlib. These are the tests that matter most: they
/// check our reader against bytes written by an implementation we did not write. A
/// round-trip through our own writer and reader would pass even if both misread the
/// format in the same way.
/// </summary>
public class BinaryPlistFixtureTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static (object? Value, JsonNode Expected) LoadFixture(string name)
    {
        object? value = BinaryPlistReader.Parse(File.ReadAllBytes(FixturePath($"{name}.plist")));
        JsonNode expected = JsonNode.Parse(File.ReadAllText(FixturePath($"{name}.json")))!;
        return (value, expected);
    }

    [Theory]
    [InlineData("ask")]
    [InlineData("types")]
    public void Reader_agrees_with_plistlib(string fixture)
    {
        var (value, expected) = LoadFixture(fixture);

        Assert.True(
            JsonNode.DeepEquals(Canonical.ToNode(value), expected),
            $"Decoded fixture '{fixture}' did not match plistlib.\n"
            + $"ours:     {Canonical.ToNode(value).ToJsonString()}\n"
            + $"plistlib: {expected.ToJsonString()}");
    }

    [Fact]
    public void Ask_fixture_decodes_to_the_expected_shape()
    {
        var (value, _) = LoadFixture("ask");
        var root = Assert.IsType<Dictionary<string, object?>>(value);

        Assert.Equal("WinDrop", root["SenderComputerName"]);
        Assert.Equal("com.apple.finder", root["BundleID"]);
        Assert.Equal(false, root["ConvertMediaFormats"]);

        var files = Assert.IsType<List<object?>>(root["Files"]);
        Assert.Equal(2, files.Count);

        var first = Assert.IsType<Dictionary<string, object?>>(files[0]);
        Assert.Equal("photo.jpg", first["FileName"]);
        Assert.Equal("public.jpeg", first["FileType"]);

        // Written by plistlib as the integer 0, not the boolean false. The distinction
        // is real in the format and the reader must preserve it.
        Assert.Equal(0L, first["ConvertMediaFormats"]);
    }

    [Fact]
    public void Integer_widths_decode_across_every_boundary()
    {
        var (value, _) = LoadFixture("types");
        var root = Assert.IsType<Dictionary<string, object?>>(value);

        Assert.Equal(0L, root["int_zero"]);
        Assert.Equal(255L, root["int_255"]);
        Assert.Equal(256L, root["int_256"]);
        Assert.Equal(65535L, root["int_65535"]);
        Assert.Equal(65536L, root["int_65536"]);
        Assert.Equal(4294967295L, root["int_max_u32"]);
        Assert.Equal(4294967296L, root["int_over_u32"]);

        // The format stores 1, 2 and 4 byte integers unsigned, so a negative value can
        // only appear in the 8-byte form. Getting this wrong turns -1 into 255.
        Assert.Equal(-1L, root["int_negative"]);
        Assert.Equal(long.MinValue, root["int_int64_min"]);
    }

    [Fact]
    public void Strings_data_and_dates_decode()
    {
        var (value, _) = LoadFixture("types");
        var root = Assert.IsType<Dictionary<string, object?>>(value);

        Assert.Equal("plain ascii string", root["ascii"]);
        Assert.Equal("", root["empty_string"]);

        // Non-ASCII forces the UTF-16BE string type, whose count is in code units
        // rather than bytes. The emoji is a surrogate pair, so code units != characters.
        Assert.Equal("Përshëndetje 世界 \U0001F4E1", root["unicode"]);

        Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0xFE, 0xFF }, root["data"]);
        Assert.Equal(Array.Empty<byte>(), root["empty_data"]);

        var date = Assert.IsType<DateTime>(root["date"]);
        Assert.Equal(new DateTime(2026, 9, 2, 14, 20, 32, DateTimeKind.Utc), date);
    }

    [Fact]
    public void Count_escape_is_followed_for_collections_longer_than_fourteen()
    {
        var (value, _) = LoadFixture("types");
        var root = Assert.IsType<Dictionary<string, object?>>(value);

        // 20 elements cannot fit the 4-bit count, so the writer used the 0xF escape.
        var longArray = Assert.IsType<List<object?>>(root["long_array"]);
        Assert.Equal(20, longArray.Count);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (object?)(long)i), longArray);

        Assert.Empty(Assert.IsType<List<object?>>(root["empty_array"]));
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(root["empty_dict"]));
    }
}

public class BinaryPlistRoundTripTests
{
    private static object? RoundTrip(object? value) =>
        BinaryPlistReader.Parse(BinaryPlistWriter.Write(value));

    [Fact]
    public void Scalars_survive_a_round_trip()
    {
        Assert.Equal(true, RoundTrip(true));
        Assert.Equal(false, RoundTrip(false));
        Assert.Equal("hello", RoundTrip("hello"));
        Assert.Equal(3.5, RoundTrip(3.5));
        Assert.Equal(new byte[] { 1, 2, 3 }, RoundTrip(new byte[] { 1, 2, 3 }));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(255L)]
    [InlineData(256L)]
    [InlineData(65535L)]
    [InlineData(65536L)]
    [InlineData(4294967295L)]
    [InlineData(4294967296L)]
    [InlineData(-1L)]
    [InlineData(-256L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Integers_survive_every_width_boundary(long value)
    {
        Assert.Equal(value, RoundTrip(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ascii")]
    [InlineData("Përshëndetje")]
    [InlineData("世界")]
    [InlineData("\U0001F4E1")]
    [InlineData("mixed ascii and 世界")]
    public void Strings_survive_both_encodings(string value)
    {
        Assert.Equal(value, RoundTrip(value));
    }

    [Fact]
    public void Nested_structures_survive()
    {
        var original = new Dictionary<string, object?>
        {
            ["name"] = "WinDrop",
            ["count"] = 2L,
            ["flag"] = true,
            ["items"] = new List<object?>
            {
                new Dictionary<string, object?> { ["a"] = 1L },
                new Dictionary<string, object?> { ["b"] = new List<object?> { "x", "y" } },
            },
        };

        Assert.True(JsonNode.DeepEquals(
            Canonical.ToNode(RoundTrip(original)),
            Canonical.ToNode(original)));
    }

    [Fact]
    public void Collections_longer_than_fourteen_use_the_count_escape()
    {
        var big = Enumerable.Range(0, 300).Select(i => (object?)(long)i).ToList();
        var result = Assert.IsType<List<object?>>(RoundTrip(big));

        Assert.Equal(300, result.Count);
        Assert.Equal(299L, result[299]);
    }

    [Fact]
    public void Repeated_scalars_are_stored_once()
    {
        // Deduplication is observable in the output size: a hundred copies of one string
        // must not cost a hundred copies of its bytes.
        var repeated = Enumerable.Repeat((object?)"a-fairly-long-repeated-string", 100).ToList();
        byte[] encoded = BinaryPlistWriter.Write(repeated);

        Assert.True(
            encoded.Length < 29 * 100,
            $"Expected deduplication to keep this under {29 * 100} bytes, got {encoded.Length}.");

        Assert.Equal(100, Assert.IsType<List<object?>>(BinaryPlistReader.Parse(encoded)).Count);
    }

    [Fact]
    public void Numeric_types_normalise_onto_the_two_the_format_has()
    {
        var mixed = new List<object?> { (int)1, (short)1, (byte)1, 1L };
        var result = Assert.IsType<List<object?>>(RoundTrip(mixed));

        Assert.All(result, item => Assert.Equal(1L, item));
    }

    [Fact]
    public void Cycles_are_refused_rather_than_hanging()
    {
        var cycle = new List<object?>();
        cycle.Add(cycle);

        Assert.Throws<PlistFormatException>(() => BinaryPlistWriter.Write(cycle));
    }
}

/// <summary>
/// A receiver decodes the /Ask body from an unauthenticated peer before showing any
/// consent prompt, so malformed input is the normal case to design for, not an edge
/// case. Every one of these must fail cleanly.
/// </summary>
public class BinaryPlistMalformedTests
{
    private static byte[] ValidPlist() =>
        BinaryPlistWriter.Write(new Dictionary<string, object?> { ["a"] = "b" });

    [Fact]
    public void Empty_input_is_rejected() =>
        Assert.Throws<PlistFormatException>(() => BinaryPlistReader.Parse([]));

    [Fact]
    public void Wrong_magic_is_rejected() =>
        Assert.Throws<PlistFormatException>(() =>
            BinaryPlistReader.Parse(Encoding.ASCII.GetBytes(new string('x', 64))));

    [Fact]
    public void Unsupported_version_is_rejected()
    {
        byte[] data = ValidPlist();
        data[6] = (byte)'1';
        data[7] = (byte)'5';

        Assert.Throws<PlistFormatException>(() => BinaryPlistReader.Parse(data));
    }

    [Fact]
    public void Truncation_is_rejected()
    {
        byte[] data = ValidPlist();
        Assert.Throws<PlistFormatException>(() => BinaryPlistReader.Parse(data[..(data.Length - 8)]));
    }

    [Fact]
    public void Root_index_past_the_object_table_is_rejected()
    {
        byte[] data = ValidPlist();
        data[data.Length - 9] = 0xFF; // low byte of topObject

        Assert.Throws<PlistFormatException>(() => BinaryPlistReader.Parse(data));
    }

    [Fact]
    public void Absurd_object_count_is_rejected_before_allocating()
    {
        byte[] data = ValidPlist();

        // numObjects = 0x00FFFFFFFFFFFFFF. A reader that allocates from this value
        // before checking it against the file length is a denial-of-service vector.
        for (int i = 1; i < 8; i++)
            data[data.Length - 24 + i] = 0xFF;

        Assert.Throws<PlistFormatException>(() => BinaryPlistReader.Parse(data));
    }

    [Fact]
    public void Offset_table_outside_the_file_is_rejected()
    {
        byte[] data = ValidPlist();

        for (int i = 4; i < 8; i++)
            data[data.Length - 8 + i] = 0xFF;

        Assert.Throws<PlistFormatException>(() => BinaryPlistReader.Parse(data));
    }

    [Fact]
    public void Zero_offset_int_size_is_rejected()
    {
        byte[] data = ValidPlist();
        data[data.Length - 26] = 0;

        Assert.Throws<PlistFormatException>(() => BinaryPlistReader.Parse(data));
    }
}

/// <summary>
/// The differential test in the direction that matters for interoperability: bytes we
/// emit must be readable by an implementation we did not write. Skipped when Python is
/// unavailable, since the codec tests above already stand on their own.
/// </summary>
public class BinaryPlistOracleTests
{
    private static string? FindPython()
    {
        string? configured = Environment.GetEnvironmentVariable("WINDROP_PYTHON");
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            return configured;

        string local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Python", "Python312", "python.exe");

        return File.Exists(local) ? local : null;
    }

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinDrop.sln")))
            dir = dir.Parent;

        return dir?.FullName;
    }

    [Fact]
    public void Plistlib_can_read_what_we_write()
    {
        string? python = FindPython();
        string? root = RepoRoot();

        Console.WriteLine($"[oracle] python={python ?? "NOT FOUND"}");
        Console.WriteLine($"[oracle] repo={root ?? "NOT FOUND"}");
        if (python is null || root is null)
            return; // oracle unavailable; the hermetic tests still cover the codec

        var payload = new Dictionary<string, object?>
        {
            ["SenderComputerName"] = "WinDrop",
            ["ConvertMediaFormats"] = false,
            ["Count"] = 65536L,
            ["Negative"] = -1L,
            ["Unicode"] = "Përshëndetje 世界",
            ["Blob"] = new byte[] { 0, 1, 2, 0xFE, 0xFF },
            ["Files"] = new List<object?>
            {
                new Dictionary<string, object?> { ["FileName"] = "photo.jpg" },
            },
        };

        string temp = Path.Combine(Path.GetTempPath(), $"windrop-{Guid.NewGuid():N}.plist");

        try
        {
            File.WriteAllBytes(temp, BinaryPlistWriter.Write(payload));

            var process = Process.Start(new ProcessStartInfo(python)
            {
                ArgumentList = { Path.Combine(root, "tools", "plist_oracle.py"), "dump", temp },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            })!;

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"plistlib failed to read our output:\n{error}");

            Assert.True(
                JsonNode.DeepEquals(JsonNode.Parse(output), Canonical.ToNode(payload)),
                $"plistlib read our bytes differently than we meant them.\nplistlib: {output}");
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
