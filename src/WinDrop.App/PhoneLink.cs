using System.IO;
using System.Windows;
using System.Windows.Media;
using WinDrop.Protocol.Web;

namespace WinDrop.App;

/// <summary>
/// Where the app keeps the phone page's secret between runs.
///
/// Kept, unlike the CLI's, because a Shortcut on the phone has the address written into
/// it, and a secret that changed at every start would break it every time. "New link"
/// replaces it, which is how a person takes the address back from a phone they no longer
/// want sending.
/// </summary>
internal static class PhoneLinkStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDrop", "phone-page.token");

    public static string LoadOrCreate()
    {
        try
        {
            string saved = File.ReadAllText(FilePath).Trim();

            // Exactly what NewToken makes; anything else is not ours, or is damaged.
            if (saved.Length == 22 && saved.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // None yet, or unreadable: make a new one.
        }

        return Renew();
    }

    public static string Renew()
    {
        string token = PhonePageServer.NewToken();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still usable for this run; a Shortcut will need updating after a restart.
        }

        return token;
    }
}

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
