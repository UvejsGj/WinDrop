using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using WinDrop.Protocol.Archive;
using WinDrop.Protocol.Compression;
using WinDrop.Protocol.Discovery;
using Xunit;

namespace WinDrop.Protocol.Tests;

public class CpioTests
{
    private static async Task<byte[]> BuildAsync(params (string Name, string Content)[] files)
    {
        var output = new MemoryStream();
        var writer = new CpioWriter(output);

        foreach (var (name, content) in files)
            await writer.WriteFileAsync(name, Encoding.UTF8.GetBytes(content));

        await writer.CompleteAsync();
        return output.ToArray();
    }

    private static async Task<List<(string Name, string Content)>> ReadAllAsync(byte[] archive)
    {
        var reader = new CpioReader(new MemoryStream(archive));
        var results = new List<(string, string)>();

        while (await reader.ReadNextAsync() is { } entry)
            results.Add((entry.Name, Encoding.UTF8.GetString(await reader.ReadContentAsync())));

        return results;
    }

    [Fact]
    public async Task Round_trips_a_single_file()
    {
        byte[] archive = await BuildAsync(("./photo.jpg", "binary-ish content"));
        var entries = await ReadAllAsync(archive);

        Assert.Single(entries);
        Assert.Equal("./photo.jpg", entries[0].Name);
        Assert.Equal("binary-ish content", entries[0].Content);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("abc")]
    [InlineData("abcd")]
    [InlineData("abcde")]
    public async Task Data_padding_holds_at_every_alignment(string content)
    {
        // Content lengths 1..5 cover every remainder mod 4. A padding bug here leaves
        // the first entry readable and everything after it garbage, so the second file
        // is the real assertion.
        byte[] archive = await BuildAsync(("./first.txt", content), ("./second.txt", "sentinel"));
        var entries = await ReadAllAsync(archive);

        Assert.Equal(2, entries.Count);
        Assert.Equal(content, entries[0].Content);
        Assert.Equal("sentinel", entries[1].Content);
    }

    [Theory]
    [InlineData("./a.txt")]
    [InlineData("./ab.txt")]
    [InlineData("./abc.txt")]
    [InlineData("./abcd.txt")]
    public async Task Name_padding_holds_at_every_alignment(string name)
    {
        // The name pad is counted from the start of the 110-byte header, not from the
        // start of the name, which is the easy thing to get wrong.
        byte[] archive = await BuildAsync((name, "x"), ("./sentinel.txt", "ok"));
        var entries = await ReadAllAsync(archive);

        Assert.Equal(name, entries[0].Name);
        Assert.Equal("ok", entries[1].Content);
    }

    [Fact]
    public async Task Every_header_starts_on_a_four_byte_boundary()
    {
        byte[] archive = await BuildAsync(("./a", "1"), ("./bb", "22"), ("./ccc", "333"));

        for (int i = 0; i + 6 <= archive.Length; i++)
        {
            if (Encoding.ASCII.GetString(archive, i, 6) != "070701") continue;
            Assert.True(i % 4 == 0, $"Header at offset {i} is not 4-byte aligned.");
        }
    }

    [Fact]
    public async Task Archive_ends_with_the_trailer_member()
    {
        byte[] archive = await BuildAsync(("./a.txt", "x"));
        Assert.Contains("TRAILER!!!", Encoding.ASCII.GetString(archive));
    }

    [Fact]
    public async Task Unicode_names_survive()
    {
        byte[] archive = await BuildAsync(("./Përshëndetje 世界.txt", "hi"));
        var entries = await ReadAllAsync(archive);

        Assert.Equal("./Përshëndetje 世界.txt", entries[0].Name);
    }

    [Fact]
    public async Task Empty_files_are_preserved()
    {
        byte[] archive = await BuildAsync(("./empty.txt", ""), ("./after.txt", "still here"));
        var entries = await ReadAllAsync(archive);

        Assert.Equal("", entries[0].Content);
        Assert.Equal("still here", entries[1].Content);
    }

    [Fact]
    public async Task Skipping_content_leaves_the_stream_aligned()
    {
        byte[] archive = await BuildAsync(("./skip.txt", "abcde"), ("./read.txt", "target"));
        var reader = new CpioReader(new MemoryStream(archive));

        CpioEntry first = (await reader.ReadNextAsync())!;
        Assert.Equal("./skip.txt", first.Name);

        // Deliberately do not read the content — the reader must skip it and its pad.
        CpioEntry second = (await reader.ReadNextAsync())!;
        Assert.Equal("target", Encoding.UTF8.GetString(await reader.ReadContentAsync()));
    }

    [Fact]
    public async Task Missing_trailer_is_reported()
    {
        var output = new MemoryStream();
        var writer = new CpioWriter(output);
        await writer.WriteFileAsync("./a.txt", "x"u8.ToArray());
        // deliberately no CompleteAsync

        var reader = new CpioReader(new MemoryStream(output.ToArray()));
        await reader.ReadNextAsync();

        await Assert.ThrowsAsync<CpioFormatException>(() => reader.ReadNextAsync());
    }

    [Fact]
    public async Task Bad_magic_is_rejected()
    {
        byte[] archive = await BuildAsync(("./a.txt", "x"));
        archive[0] = (byte)'X';

        var reader = new CpioReader(new MemoryStream(archive));
        await Assert.ThrowsAsync<CpioFormatException>(() => reader.ReadNextAsync());
    }

    [Fact]
    public async Task Non_hex_header_field_is_rejected()
    {
        byte[] archive = await BuildAsync(("./a.txt", "x"));
        archive[6 + 6 * 8] = (byte)'z'; // corrupt the filesize field

        var reader = new CpioReader(new MemoryStream(archive));
        await Assert.ThrowsAsync<CpioFormatException>(() => reader.ReadNextAsync());
    }
}

/// <summary>
/// bsdtar/libarchive reads our archives. Same reasoning as the plistlib oracle: our own
/// reader agreeing with our own writer proves only that they share any misunderstanding.
/// </summary>
public class CpioOracleTests
{
    private static string? FindTar()
    {
        string system = Path.Combine(Environment.SystemDirectory, "tar.exe");
        return File.Exists(system) ? system : null;
    }

    [Fact]
    public async Task Bsdtar_can_extract_what_we_write()
    {
        string? tar = FindTar();
        Console.WriteLine($"[oracle] tar={tar ?? "NOT FOUND"}");
        if (tar is null) return;

        string dir = Path.Combine(Path.GetTempPath(), $"windrop-cpio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            string archivePath = Path.Combine(dir, "payload.cpio");

            // Odd lengths on purpose so both padding rules are exercised in the archive
            // an independent implementation has to parse.
            var expected = new Dictionary<string, string>
            {
                ["./photo.jpg"] = "seven..",
                ["./notes.txt"] = "a",
                ["./deeper/file.bin"] = new string('z', 4093),
            };

            await using (var file = File.Create(archivePath))
            {
                var writer = new CpioWriter(file);
                await writer.WriteDirectoryAsync("./deeper");

                foreach (var (name, content) in expected)
                    await writer.WriteFileAsync(name, Encoding.UTF8.GetBytes(content));

                await writer.CompleteAsync();
            }

            string extractDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(extractDir);

            var process = Process.Start(new ProcessStartInfo(tar)
            {
                ArgumentList = { "-x", "-f", archivePath, "-C", extractDir },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;

            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.True(process.ExitCode == 0, $"bsdtar refused our archive:\n{stderr}");

            foreach (var (name, content) in expected)
            {
                string extracted = Path.Combine(extractDir, name.Replace("./", "").Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(extracted), $"bsdtar did not extract {name}");
                Assert.Equal(content, await File.ReadAllTextAsync(extracted));
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task We_can_read_what_bsdtar_writes()
    {
        string? tar = FindTar();
        if (tar is null) return;

        string dir = Path.Combine(Path.GetTempPath(), $"windrop-cpio-in-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "alpha.txt"), "first");
            await File.WriteAllTextAsync(Path.Combine(dir, "beta.txt"), "second-and-longer");

            string archivePath = Path.Combine(dir, "made-by-tar.cpio");

            var process = Process.Start(new ProcessStartInfo(tar)
            {
                ArgumentList = { "-c", "--format", "newc", "-f", archivePath, "-C", dir, "alpha.txt", "beta.txt" },
                RedirectStandardError = true,
            })!;

            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"bsdtar failed to create an archive:\n{stderr}");

            await using var file = File.OpenRead(archivePath);
            var reader = new CpioReader(file);
            var seen = new Dictionary<string, string>();

            while (await reader.ReadNextAsync() is { } entry)
                seen[entry.Name] = Encoding.UTF8.GetString(await reader.ReadContentAsync());

            Assert.Equal("first", seen["alpha.txt"]);
            Assert.Equal("second-and-longer", seen["beta.txt"]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}

/// <summary>
/// A trap worth a test of its own: in libarchive, "--format cpio" means odc (POSIX.1,
/// magic 070707), NOT the newc/SVR4 variant (magic 070701) that AirDrop uses. Anyone
/// verifying an archive by hand with the obvious command gets the wrong format and a
/// confusing mismatch. Rejecting odc loudly is correct behaviour, so it is pinned here.
/// </summary>
/// <summary>
/// Both cpio variants must be readable. In libarchive, the format name "cpio" selects
/// odc (magic 070707), NOT the newc/SVR4 variant (070701) that we write — and opendrop
/// takes that default, so its uploads arrive as odc. Since opendrop interoperates with
/// real Apple devices, rejecting odc would be stricter than the protocol actually is.
///
/// The two differ in more than a magic number: newc uses hexadecimal fields in a
/// 110-byte header and pads both name and data to four bytes; odc uses octal fields in a
/// 76-byte header and pads nothing.
/// </summary>
public class CpioVariantTests
{
    private static string? FindTar()
    {
        string system = Path.Combine(Environment.SystemDirectory, "tar.exe");
        return File.Exists(system) ? system : null;
    }

    [Theory]
    [InlineData("newc")]
    [InlineData("odc")]
    public async Task Both_variants_are_read(string format)
    {
        string? tar = FindTar();
        if (tar is null) return;

        string dir = Path.Combine(Path.GetTempPath(), $"windrop-{format}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            // Odd lengths so the padding rules diverge between the two formats: newc
            // pads these, odc does not, and a reader that applies the wrong rule
            // desynchronises on the second entry rather than the first.
            await File.WriteAllTextAsync(Path.Combine(dir, "alpha.txt"), "12345");
            await File.WriteAllTextAsync(Path.Combine(dir, "beta.txt"), "second-and-longer");

            string archivePath = Path.Combine(dir, $"{format}.cpio");

            var process = Process.Start(new ProcessStartInfo(tar)
            {
                ArgumentList = { "-c", "--format", format, "-f", archivePath, "-C", dir, "alpha.txt", "beta.txt" },
                RedirectStandardError = true,
            })!;

            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"bsdtar could not write {format}:\n{stderr}");

            await using var file = File.OpenRead(archivePath);
            var reader = new CpioReader(file);
            var seen = new Dictionary<string, string>();

            while (await reader.ReadNextAsync() is { } entry)
                seen[entry.Name] = Encoding.UTF8.GetString(await reader.ReadContentAsync());

            Assert.Equal("12345", seen["alpha.txt"]);
            Assert.Equal("second-and-longer", seen["beta.txt"]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task An_unknown_magic_names_both_variants_in_the_error()
    {
        var output = new MemoryStream();
        var writer = new CpioWriter(output);
        await writer.WriteFileAsync("./a.txt", "x"u8.ToArray());
        await writer.CompleteAsync();

        byte[] archive = output.ToArray();
        archive[3] = (byte)'9'; // 070701 -> 070901, neither variant

        var reader = new CpioReader(new MemoryStream(archive));
        var error = await Assert.ThrowsAsync<CpioFormatException>(() => reader.ReadNextAsync());

        Assert.Contains("070701", error.Message);
        Assert.Contains("070707", error.Message);
    }
}

public class DvZipTests
{
    private static byte[] Compressible(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)('a' + (i % 26));
        return data;
    }

    private static async Task<byte[]> CompressAsync(byte[] input, int blockSize = DvZip.DefaultBlockSize)
    {
        var output = new MemoryStream();
        await DvZip.CompressAsync(new MemoryStream(input), output, blockSize);
        return output.ToArray();
    }

    private static async Task<byte[]> DecompressAsync(byte[] input)
    {
        var output = new MemoryStream();
        await DvZip.DecompressAsync(new MemoryStream(input), output);
        return output.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(300000)]
    public async Task Round_trips_at_and_across_block_boundaries(int length)
    {
        byte[] original = Compressible(length);
        Assert.Equal(original, await DecompressAsync(await CompressAsync(original)));
    }

    [Fact]
    public async Task Each_block_is_independently_valid_zlib()
    {
        // The point of the framing: a receiver can inflate block N without having seen
        // block N-1. If blocks shared deflate state this would fail.
        byte[] compressed = await CompressAsync(Compressible(200_000), blockSize: 64 * 1024);

        int offset = 0;
        int blocks = 0;
        var recovered = new MemoryStream();

        while (offset < compressed.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(compressed.AsSpan(offset, 4));
            offset += 4;

            using var block = new MemoryStream(compressed, offset, (int)length, writable: false);
            await using var inflate = new ZLibStream(block, CompressionMode.Decompress);
            await inflate.CopyToAsync(recovered);

            offset += (int)length;
            blocks++;
        }

        Assert.True(blocks >= 4, $"Expected the payload to span several blocks, got {blocks}.");
        Assert.Equal(Compressible(200_000), recovered.ToArray());
    }

    [Fact]
    public async Task Compression_actually_compresses()
    {
        byte[] compressed = await CompressAsync(Compressible(200_000));
        Assert.True(compressed.Length < 200_000 / 4, $"Expected real compression, got {compressed.Length} bytes.");
    }

    [Fact]
    public async Task Oversized_block_length_is_refused_before_allocating()
    {
        var hostile = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(hostile, 0xFFFFFFFF);

        await Assert.ThrowsAsync<DvZipFormatException>(() => DecompressAsync(hostile));
    }

    [Fact]
    public async Task Zero_length_block_is_refused()
    {
        var hostile = new byte[4]; // length 0
        await Assert.ThrowsAsync<DvZipFormatException>(() => DecompressAsync(hostile));
    }

    [Fact]
    public async Task Truncated_block_is_refused()
    {
        byte[] compressed = await CompressAsync(Compressible(1000));
        await Assert.ThrowsAsync<DvZipFormatException>(() => DecompressAsync(compressed[..^5]));
    }

    [Fact]
    public async Task Truncated_length_header_is_refused()
    {
        byte[] compressed = await CompressAsync(Compressible(1000));
        await Assert.ThrowsAsync<DvZipFormatException>(() => DecompressAsync(compressed[..2]));
    }
}

public class CompressionNegotiationTests
{
    [Fact]
    public void DvZip_is_chosen_only_when_the_peer_advertises_it()
    {
        Assert.True(AirDropCompression.ShouldUseDvZip(AirDropReceiverFlags.SupportsDvZip));
        Assert.False(AirDropCompression.ShouldUseDvZip(AirDropReceiverFlags.SupportsUrl));
        Assert.False(AirDropCompression.ShouldUseDvZip(AirDropReceiverFlags.None));
    }

    [Theory]
    [InlineData(AirDropReceiverFlags.SupportsDvZip)]
    [InlineData(AirDropReceiverFlags.None)]
    public async Task Both_encodings_round_trip_through_the_negotiated_path(AirDropReceiverFlags flags)
    {
        byte[] original = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("payload ", 5000)));

        var compressed = new MemoryStream();
        await AirDropCompression.CompressAsync(new MemoryStream(original), compressed, flags);

        compressed.Position = 0;
        var restored = new MemoryStream();
        await AirDropCompression.DecompressAsync(
            compressed, restored, AirDropCompression.ShouldUseDvZip(flags));

        Assert.Equal(original, restored.ToArray());
    }

    [Fact]
    public async Task A_cpio_archive_survives_the_full_upload_encoding()
    {
        // The real /Upload body: files -> cpio -> DVZip. Composing the two layers is
        // where an off-by-one in either shows up.
        var archive = new MemoryStream();
        var writer = new CpioWriter(archive);
        await writer.WriteFileAsync("./photo.jpg", Encoding.UTF8.GetBytes(new string('p', 5001)));
        await writer.WriteFileAsync("./notes.txt", "short"u8.ToArray());
        await writer.CompleteAsync();

        byte[] plain = archive.ToArray();

        var compressed = new MemoryStream();
        await DvZip.CompressAsync(new MemoryStream(plain), compressed);

        compressed.Position = 0;
        var restored = new MemoryStream();
        await DvZip.DecompressAsync(compressed, restored);

        Assert.Equal(plain, restored.ToArray());

        restored.Position = 0;
        var reader = new CpioReader(restored);

        CpioEntry first = (await reader.ReadNextAsync())!;
        Assert.Equal("./photo.jpg", first.Name);
        Assert.Equal(5001, (await reader.ReadContentAsync()).Length);

        CpioEntry second = (await reader.ReadNextAsync())!;
        Assert.Equal("./notes.txt", second.Name);

        Assert.Null(await reader.ReadNextAsync());
    }
}
