using WinDrop.Protocol.Plist;

namespace WinDrop.Protocol.Tests;

/// <summary>
/// The /Ask preview image.
///
/// The fixture was not written by us. It is an /Ask body assembled the way opendrop's
/// client.send_ask assembles one, with an icon from its generate_file_icon encoder,
/// serialised by Python's plistlib (see tools/fileicon_oracle.py). So the first test
/// checks our reader against an independent writer, not against our own idea of one.
/// </summary>
public class FileIconTests
{
    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void An_opendrop_ask_body_yields_its_icon()
    {
        object? parsed = BinaryPlistReader.Parse(Fixture("opendrop-ask-with-icon.bplist"));
        var ask = AirDropAskRequest.FromPlist(Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(parsed));

        Assert.Equal("opendrop-oracle", ask.SenderComputerName);
        Assert.Equal("photo.jpg", Assert.Single(ask.Files).FileName);

        // 19 KB is big enough to matter: the data object's length overflows the marker
        // nibble into a separate integer, and the offset table needs two-byte entries.
        // Small hand-written test values never reach either path.
        Assert.NotNull(ask.FileIcon);
        Assert.Equal(19276, ask.FileIcon.Length);
        Assert.Equal(PreviewImageFormat.Jpeg2000, PreviewImage.Sniff(ask.FileIcon));
    }

    [Fact]
    public void An_icon_survives_our_own_writer_and_reader()
    {
        byte[] icon = new byte[70_000];
        Random.Shared.NextBytes(icon);

        var ask = new AirDropAskRequest("PC", "Windows", "id", AirDropAskRequest.FinderBundleId,
            [AirDropFileEntry.ForFile("photo.jpg")], FileIcon: icon);

        object? parsed = BinaryPlistReader.Parse(BinaryPlistWriter.Write(ask.ToPlist()));
        var back = AirDropAskRequest.FromPlist(Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(parsed));

        Assert.Equal(icon, back.FileIcon);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_preview_means_no_key_at_all(bool emptyArray)
    {
        var ask = new AirDropAskRequest("PC", "Windows", "id", AirDropAskRequest.FinderBundleId,
            [AirDropFileEntry.ForFile("notes.txt")], FileIcon: emptyArray ? [] : null);

        Assert.False(ask.ToPlist().ContainsKey("FileIcon"));
    }

    [Fact]
    public void An_icon_of_the_wrong_type_is_dropped()
    {
        var plist = new Dictionary<string, object?>
        {
            ["SenderComputerName"] = "PC",
            ["Files"] = new List<object?>(),
            ["FileIcon"] = "definitely not image bytes",
        };

        object? parsed = BinaryPlistReader.Parse(BinaryPlistWriter.Write(plist));
        var ask = AirDropAskRequest.FromPlist(Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(parsed));

        Assert.Null(ask.FileIcon);
    }

    [Theory]
    // JPEG: SOI then the APP0 marker.
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 }, PreviewImageFormat.Jpeg)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 }, PreviewImageFormat.Png)]
    // The JP2 signature box, byte for byte as opendrop's encoder writes it.
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A, 0x00 }, PreviewImageFormat.Jpeg2000)]
    // A bare codestream: SOC, SIZ.
    [InlineData(new byte[] { 0xFF, 0x4F, 0xFF, 0x51, 0x00 }, PreviewImageFormat.Jpeg2000)]
    public void Formats_are_named_by_their_signatures(byte[] head, PreviewImageFormat expected)
    {
        Assert.Equal(expected, PreviewImage.Sniff(head));
    }

    [Theory]
    [InlineData(new byte[0])]
    // SOI alone is too little to commit a decoder to.
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    // A JP2 signature cut short.
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20 })]
    // The same signature after something converted its CRLF to LF in transit — the
    // corruption that 0D 0A in the signature exists to expose.
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0A, 0x87, 0x0A, 0x00 })]
    // GIF89a: a real image, but not one we will hand to a decoder.
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]
    public void Anything_else_is_unknown(byte[] head)
    {
        Assert.Equal(PreviewImageFormat.Unknown, PreviewImage.Sniff(head));
    }
}
