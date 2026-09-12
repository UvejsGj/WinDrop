using System.Text.Json;

namespace WinDrop.Protocol.Tests;

/// <summary>
/// The <c>FileType</c> an /Ask entry carries.
///
/// Half of these check our own table. The other half check it against
/// <c>opendrop-uti.json</c>, which is not a transcription of opendrop's source but the
/// output of its classifier, run over every signature fleep knows (tools/uti_oracle.py).
/// That distinction earns its keep here: opendrop's source reads as though it produces
/// <c>public.jpeg</c> and <c>public.camera-raw-image</c>, and it never produces either.
/// </summary>
public class FileTypeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"windrop-uti-{Guid.NewGuid():N}");

    public FileTypeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private sealed record OpendropRow(string Extension, string Signature, string? Mime, string? Type, string Uti);

    private static readonly IReadOnlyList<OpendropRow> Opendrop =
        JsonSerializer.Deserialize<List<OpendropRow>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "opendrop-uti.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    /// <summary>
    /// Every verdict opendrop reaches for this extension. More than one means its
    /// signatures disagree with each other; none means fleep does not know the format.
    /// </summary>
    private static string[] OpendropUtis(string extension) =>
        Opendrop.Where(r => r.Extension == extension).Select(r => r.Uti).Distinct().Order(StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("holiday.jpg", "public.jpeg")]
    [InlineData("holiday.jpeg", "public.jpeg")]
    [InlineData("diagram.png", "public.png")]
    [InlineData("loop.gif", "com.compuserve.gif")]
    [InlineData("IMG_4021.HEIC", "public.heic")]
    [InlineData("clip.mp4", "public.mpeg-4")]
    [InlineData("clip.mov", "com.apple.quicktime-movie")]
    [InlineData("track.m4a", "public.mpeg-4-audio")]
    [InlineData("notes.txt", "public.plain-text")]
    [InlineData("report.pdf", "com.adobe.pdf")]
    [InlineData("sheet.xlsx", "org.openxmlformats.spreadsheetml.sheet")]
    [InlineData("shot.CR2", "public.camera-raw-image")]
    [InlineData("bundle.zip", "public.zip-archive")]
    public void A_known_extension_names_the_format(string fileName, string expected)
    {
        Assert.Equal(expected, UniformTypeIdentifiers.ForFileName(fileName));
    }

    [Theory]
    [InlineData("README")]
    [InlineData("data.qqq")]
    [InlineData(".gitignore")]
    [InlineData("trailing.")]
    public void Anything_unrecognised_stays_an_opaque_byte_stream(string fileName)
    {
        Assert.Equal(AirDropFileEntry.DefaultFileType, UniformTypeIdentifiers.ForFileName(fileName));
    }

    [Fact]
    public void Only_the_last_extension_is_read()
    {
        // A .tar.gz is named for both layers, and only the outer one gets a verdict. That
        // is not a loss: the file really is a gzip archive, and a receiver unpacking it
        // finds the tar underneath regardless of what we called it.
        Assert.Equal("org.gnu.gnu-zip-archive", UniformTypeIdentifiers.ForFileName("backup.tar.gz"));
        Assert.Equal("org.gnu.gnu-zip-tar-archive", UniformTypeIdentifiers.ForFileName("backup.tgz"));
    }

    [Fact]
    public void An_entry_types_itself_from_the_name_the_receiver_will_see()
    {
        string path = Path.Combine(_root, "holiday.jpg");
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xE0]);

        AirDropFileEntry entry = AirDropOutgoingFile.FromPath(path).ToEntry();

        Assert.Equal("public.jpeg", entry.FileType);
        Assert.Equal("holiday.jpg", entry.FileName);
        Assert.False(entry.IsDirectory);
    }

    [Fact]
    public void A_directory_is_a_folder_however_it_is_named()
    {
        // A folder called "album.jpg" is legal on Windows and is not a JPEG. The
        // directory check has to win, or the receiver is told to expect an image and
        // handed a cpio subtree.
        Directory.CreateDirectory(Path.Combine(_root, "album.jpg"));

        AirDropFileEntry entry = AirDropOutgoingFile.FromPath(Path.Combine(_root, "album.jpg")).ToEntry();

        Assert.Equal(UniformTypeIdentifiers.Folder, entry.FileType);
        Assert.True(entry.IsDirectory);
    }

    [Fact]
    public void An_explicitly_given_type_is_left_alone()
    {
        // The override is the way to put an arbitrary type on the wire — which is how a
        // receiver that disagrees with the table would be investigated, by sending the
        // same file twice under two types.
        var selection = new AirDropOutgoingFile(Path.Combine(_root, "holiday.jpg"), "holiday.jpg", "public.image");

        Assert.Equal("public.image", selection.ToEntry().FileType);
    }

    [Theory]
    [InlineData("png", "public.png")]
    [InlineData("gif", "com.compuserve.gif")]
    [InlineData("jp2", "public.jpeg-2000")]
    public void Where_opendrop_names_the_format_we_agree(string extension, string uti)
    {
        Assert.Equal(uti, Assert.Single(OpendropUtis(extension)));
        Assert.Equal(uti, UniformTypeIdentifiers.ForFileName($"file.{extension}"));
    }

    [Theory]
    // The public.jpeg branch in get_uti_type tests for "jpg" in the MIME type, and fleep's
    // MIME type is image/jpeg — "jpg" is its extension field. The branch is dead.
    [InlineData("jpg", "public.image", "public.jpeg")]
    // Raw files are TIFF containers. Sniffing cannot reach public.camera-raw-image at all,
    // whatever the source says, because the bytes are those of a TIFF.
    [InlineData("cr2", "public.image", "public.camera-raw-image")]
    [InlineData("nef", "public.image", "public.camera-raw-image")]
    [InlineData("arw", "public.image", "public.camera-raw-image")]
    [InlineData("dng", "public.image", "public.camera-raw-image")]
    // "zip" is a substring of "application/gzip", and the zip test is an if rather than an
    // elif, so it overwrites the gzip verdict that ran immediately before it.
    [InlineData("gz", "public.zip-archive", "org.gnu.gnu-zip-archive")]
    // These two are not bugs, only the ceiling of what a header can say: an ftyp box is
    // an ftyp box, and fleep's abstract answer is as far as content alone gets.
    [InlineData("mp4", "public.video", "public.mpeg-4")]
    [InlineData("m4a", "public.audio", "public.mpeg-4-audio")]
    public void Where_opendrop_loses_the_format_we_keep_it(string extension, string theirs, string ours)
    {
        Assert.Equal(theirs, Assert.Single(OpendropUtis(extension)));
        Assert.Equal(ours, UniformTypeIdentifiers.ForFileName($"file.{extension}"));
    }

    [Fact]
    public void One_format_can_sniff_as_two_things_and_still_have_one_name()
    {
        // A zip starting PK\x03\x04 matches fleep's Office-document entries, which share
        // that signature; an empty one starting PK\x05\x06 does not. So the same format
        // classifies as a document or as an archive depending on its contents.
        Assert.Equal(["public.content", "public.zip-archive"], OpendropUtis("zip"));
        Assert.Equal("public.zip-archive", UniformTypeIdentifiers.ForFileName("bundle.zip"));
    }

    [Theory]
    // HEIC is what an iPhone's own camera produces, and it is the case where routing into
    // Photos would matter most.
    [InlineData("heic", "public.heic")]
    [InlineData("svg", "public.svg-image")]
    [InlineData("txt", "public.plain-text")]
    [InlineData("csv", "public.comma-separated-values-text")]
    public void A_format_with_no_signature_at_all_is_still_named(string extension, string ours)
    {
        Assert.Empty(OpendropUtis(extension));
        Assert.Equal(ours, UniformTypeIdentifiers.ForFileName($"file.{extension}"));
    }
}
