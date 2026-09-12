using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinDrop.Protocol;

namespace WinDrop.App;

/// <summary>
/// Both directions of the /Ask <c>FileIcon</c>: making one to send, and turning a
/// stranger's into something safe to put on screen.
/// </summary>
public static class FileIconCodec
{
    /// <summary>opendrop's bounding box for the icon. Apple's own has not been observed.</summary>
    public const int Pixels = 540;

    /// <summary>
    /// Anything declaring itself larger is refused before its pixels are decoded. The /Ask
    /// body cap bounds the compressed size, not the decoded one: a megabyte of PNG can
    /// claim to be 65535 × 65535, which is 17 GB once it is BGRA.
    /// </summary>
    private const int MaxDimension = 2048;

    /// <summary>
    /// The icon to send for a selection, or null when there is no picture of its contents.
    ///
    /// JPEG, where opendrop sends JPEG 2000 — for the plain reason that Windows has no
    /// encoder for it. Whether an Apple receiver will render a JPEG in this field has not
    /// been tested; it is on the list for the first session over a real AWDL link.
    ///
    /// Only opaque thumbnails are sent. JPEG has no alpha, so a transparent PNG would reach
    /// the other side with whatever colour happened to sit under its transparent pixels;
    /// sending nothing lets the receiver fall back to its own icon for the type instead.
    /// </summary>
    public static async Task<byte[]?> CreateAsync(string path)
    {
        try
        {
            return await ShellPreview.RunOnSta(() =>
            {
                if (ShellPreview.ContentThumbnail(path, Pixels) is not { IsOpaque: true } thumbnail)
                    return null;

                var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                encoder.Frames.Add(BitmapFrame.Create(thumbnail.Image));

                using var buffer = new MemoryStream();
                encoder.Save(buffer);
                return buffer.ToArray();
            }).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // A preview is decoration. Failing to make one must never fail the send.
            return null;
        }
    }

    /// <summary>
    /// Decodes a received icon for the consent prompt, or returns null if it will not be
    /// decoded. The bytes are from a peer nobody has authenticated, and this runs before
    /// the user has said yes to anything.
    /// </summary>
    public static async Task<FilePreview?> DecodeAsync(byte[] data, int pixels)
    {
        try
        {
            return await ShellPreview.RunOnSta(() => Decode(data, pixels)).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static FilePreview? Decode(byte[] data, int pixels)
    {
        using var stream = new MemoryStream(data, writable: false);

        // Colour profiles are skipped: an embedded ICC profile is a second parser to feed
        // hostile bytes to, and for a thumbnail on a dark sheet it buys nothing visible.
        const BitmapCreateOptions options = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;

        // Named decoders only, never BitmapDecoder.Create, which sniffs and dispatches to
        // any codec on the machine that claims the bytes. See PreviewImage for why.
        BitmapDecoder? decoder = PreviewImage.Sniff(data) switch
        {
            PreviewImageFormat.Jpeg => new JpegBitmapDecoder(stream, options, BitmapCacheOption.None),
            PreviewImageFormat.Png => new PngBitmapDecoder(stream, options, BitmapCacheOption.None),

            // JPEG 2000 — what opendrop sends — is declined here rather than attempted.
            // Windows has no decoder for it, so the caller shows the type icon instead.
            _ => null,
        };

        if (decoder is null || decoder.Frames.Count == 0) return null;

        BitmapFrame frame = decoder.Frames[0];

        // Dimensions come from the header. Nothing has been decompressed yet.
        if (frame.PixelWidth is <= 0 or > MaxDimension || frame.PixelHeight is <= 0 or > MaxDimension)
            return null;

        double scale = Math.Min(1.0, (double)pixels / Math.Max(frame.PixelWidth, frame.PixelHeight));

        BitmapSource sized = scale < 1
            ? new TransformedBitmap(frame, new ScaleTransform(scale, scale))
            : frame;

        // Realise the pixels now, while the stream is still open, and detach them from it.
        var realised = new WriteableBitmap(sized);
        realised.Freeze();

        return new FilePreview(realised, IsOpaque: true);
    }
}
