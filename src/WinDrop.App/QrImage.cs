using System.Windows;
using System.Windows.Media;
using WinDrop.Protocol.Web;

namespace WinDrop.App;

/// <summary>A QR code as a vector image: crisp at any size and any display scaling.</summary>
internal static class QrImage
{
    public static DrawingImage Create(QrCode code)
    {
        // The standard's quiet zone: four light modules all round, or a scanner may not find
        // the code's edge against whatever surrounds it.
        const int quiet = 4;
        int side = code.Size + quiet * 2;

        var dark = new GeometryGroup();

        for (int row = 0; row < code.Size; row++)
        {
            for (int column = 0; column < code.Size; column++)
            {
                if (code[row, column])
                    dark.Children.Add(new RectangleGeometry(new Rect(column + quiet, row + quiet, 1, 1)));
            }
        }

        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, side, side))));
        drawing.Children.Add(new GeometryDrawing(Brushes.Black, null, dark));

        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
