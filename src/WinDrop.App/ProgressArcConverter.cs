using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WinDrop.App;

/// <summary>
/// Turns a 0..1 progress value into the arc that sweeps around a peer's avatar.
///
/// Starts at twelve o'clock and runs clockwise, which is what AirDrop does. A full circle
/// is emitted as an ellipse rather than an arc, because an ArcSegment whose start and end
/// points coincide is ambiguous — WPF draws nothing at all for it.
/// </summary>
public sealed class ProgressArcConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double progress = value is double d ? Math.Clamp(d, 0, 1) : 0;
        double size = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : 78;

        const double stroke = 3;
        double radius = size / 2 - stroke / 2;
        double centre = size / 2;

        if (progress <= 0)
            return Geometry.Empty;

        if (progress >= 1)
            return new EllipseGeometry(new Point(centre, centre), radius, radius);

        double sweep = progress * 360;
        Point start = new(centre, centre - radius);
        double endAngle = (-90 + sweep) * Math.PI / 180;
        Point end = new(centre + radius * Math.Cos(endAngle), centre + radius * Math.Sin(endAngle));

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };

        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            IsLargeArc = sweep > 180,
            SweepDirection = SweepDirection.Clockwise,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        return geometry;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Half the bound height as a uniform corner radius, which is what makes a capsule.
///
/// WPF does not clamp a Border's radius to fit the way CSS does. A radius larger than half
/// the height draws an ellipse — every "capsule" button in the first glass build came out
/// as an oval — so the radius has to be derived from the rendered height.
/// </summary>
public sealed class CapsuleRadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new CornerRadius(value is double height ? height / 2 : 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapses an element when the bound boolean is false.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
