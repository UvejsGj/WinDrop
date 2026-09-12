using System.Windows.Media.Imaging;

namespace WinDrop.App;

/// <summary>
/// One image in a preview stack, already laid out. Immutable: a new selection builds a new
/// stack rather than editing the old one, so there is nothing to notify about.
/// </summary>
public sealed class PreviewItem
{
    private PreviewItem(FilePreview preview, double box, double angle, double offsetX)
    {
        Image = preview.Image;
        IsCard = preview.IsOpaque;
        Angle = angle;
        OffsetX = offsetX;

        // A card keeps the picture's proportions inside the box. An icon is square artwork
        // with padding designed into it, so it gets the whole box.
        if (IsCard && Image.PixelWidth > 0 && Image.PixelHeight > 0)
        {
            double aspect = (double)Image.PixelWidth / Image.PixelHeight;
            Width = aspect >= 1 ? box : box * aspect;
            Height = aspect >= 1 ? box / aspect : box;
        }
        else
        {
            Width = box;
            Height = box;
        }
    }

    public BitmapSource Image { get; }
    public bool IsCard { get; }
    public bool IsIcon => !IsCard;
    public double Width { get; }
    public double Height { get; }
    public double Angle { get; }
    public double OffsetX { get; }

    /// <summary>
    /// Fans up to three previews out like a hand of cards, first file on top. Returned
    /// back to front, because later children draw over earlier ones.
    /// </summary>
    public static IReadOnlyList<PreviewItem> Stack(IReadOnlyList<FilePreview> previews, double box)
    {
        (double Angle, double Offset)[] fan = [(0, 0), (-9, -16), (9, 16)];

        return previews
            .Take(fan.Length)
            .Select((preview, i) => new PreviewItem(preview, box, fan[i].Angle, previews.Count > 1 ? fan[i].Offset : 0))
            .Reverse()
            .ToList();
    }
}
