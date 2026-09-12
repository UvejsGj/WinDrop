namespace WinDrop.Protocol;

public enum PreviewImageFormat { Unknown, Jpeg, Png, Jpeg2000 }

/// <summary>
/// Identifies how an /Ask <c>FileIcon</c> is encoded, from its leading bytes alone.
///
/// This sits in the protocol library rather than the UI because the icon is decoded
/// before the user has consented to anything, on behalf of a peer nobody has
/// authenticated. Which bytes get anywhere near an image decoder is therefore a security
/// decision, not a rendering one. The convenient "decode whatever this is" call on Windows
/// routes by content to every WIC codec installed on the machine — camera raw, HEIF, WebP,
/// whatever a driver or an app has registered — and each of those is attack surface a
/// sender could reach without the user clicking a thing. Naming the format first lets the
/// caller pick one specific in-box decoder, or none.
///
/// The format a peer actually sends is not the one you would guess. opendrop encodes its
/// icon as JPEG 2000 (<c>util.generate_file_icon</c>, 540px bounding box), presumably
/// because that is what its authors saw Apple send. Nothing on Windows decodes that out of
/// the box, so recognising it matters mostly so it can be declined cleanly rather than
/// thrown at a decoder to see what happens.
/// </summary>
public static class PreviewImage
{
    // The JP2 file format opens with a fixed 12-byte box: length 12, type "jP  ", and a
    // body of 0D 0A 87 0A chosen so that line-ending conversion or 7-bit stripping in
    // transit corrupts it detectably. Confirmed against the bytes opendrop produces.
    private static ReadOnlySpan<byte> Jp2Signature =>
        [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];

    // A bare JPEG 2000 codestream with no JP2 wrapper: SOC, then SIZ, which the standard
    // requires to come immediately after it.
    private static ReadOnlySpan<byte> J2kCodestream => [0xFF, 0x4F, 0xFF, 0x51];

    private static ReadOnlySpan<byte> PngSignature =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // SOI and the first byte of whatever marker follows it. SOI on its own is two bytes,
    // which is too little to hang a decoder choice on.
    private static ReadOnlySpan<byte> JpegStart => [0xFF, 0xD8, 0xFF];

    public static PreviewImageFormat Sniff(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(JpegStart)) return PreviewImageFormat.Jpeg;
        if (data.StartsWith(PngSignature)) return PreviewImageFormat.Png;
        if (data.StartsWith(Jp2Signature) || data.StartsWith(J2kCodestream)) return PreviewImageFormat.Jpeg2000;

        return PreviewImageFormat.Unknown;
    }
}
