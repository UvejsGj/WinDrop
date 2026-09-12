using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace WinDrop.App.Glass;

/// <summary>
/// A floating pane of Liquid Glass.
///
/// Glass is only visible because of what is behind it. A translucent rectangle over a flat
/// colour is just a slightly different flat colour, which is exactly why the previous design
/// read as grey boxes. So the window paints its own scene — light and the radar — and every
/// pane samples that scene from behind itself, blurs it, and runs it through a lens shader
/// that bends the rim, splits the colour slightly and lays a specular edge on top.
///
/// WPF cannot sample what is behind an element, so the pane asks for it explicitly: a
/// VisualBrush of the scene, with its viewbox moved every frame to the pane's own position.
/// That is also why the scene has to be a sibling, never an ancestor — a brush of a visual
/// that contains the brush would have to render itself.
/// </summary>
[ContentProperty(nameof(Child))]
public class GlassPanel : FrameworkElement
{
    /// <summary>
    /// Room around the pane for the blur to draw real pixels from, for the rim to bend light
    /// in from, and for the shadow to fall into.
    /// </summary>
    public const double Bleed = 30;

    /// <summary>Where the light is, in scene coordinates. The pointer, when there is one.</summary>
    public static Point LightPosition { get; set; } = new(40, -80);

    public static readonly DependencyProperty SceneProperty = DependencyProperty.RegisterAttached(
        "Scene", typeof(Visual), typeof(GlassPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static Visual? GetScene(DependencyObject element) => (Visual?)element.GetValue(SceneProperty);
    public static void SetScene(DependencyObject element, Visual? value) => element.SetValue(SceneProperty, value);

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(GlassPanel),
        new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsRender, OnShapeChanged));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(GlassPanel),
        new FrameworkPropertyMetadata(default(Thickness), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FrostProperty = DependencyProperty.Register(
        nameof(Frost), typeof(double), typeof(GlassPanel), new PropertyMetadata(0.3, OnOpticsChanged));

    public static readonly DependencyProperty RefractionProperty = DependencyProperty.Register(
        nameof(Refraction), typeof(double), typeof(GlassPanel), new PropertyMetadata(16.0, OnOpticsChanged));

    public static readonly DependencyProperty RimWidthProperty = DependencyProperty.Register(
        nameof(RimWidth), typeof(double), typeof(GlassPanel), new PropertyMetadata(18.0, OnOpticsChanged));

    public static readonly DependencyProperty DispersionProperty = DependencyProperty.Register(
        nameof(Dispersion), typeof(double), typeof(GlassPanel), new PropertyMetadata(0.22, OnOpticsChanged));

    public static readonly DependencyProperty BlurRadiusProperty = DependencyProperty.Register(
        nameof(BlurRadius), typeof(double), typeof(GlassPanel), new PropertyMetadata(14.0, OnBlurChanged));

    /// <summary>0..1. Lifts the pane for hover and drag feedback; animatable.</summary>
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(
        nameof(Highlight), typeof(double), typeof(GlassPanel), new PropertyMetadata(0.0, OnLightChanged));

    private readonly Grid _lens = new() { IsHitTestVisible = false };

    // A blur grows the area an element renders into by its radius, and the lens shader is
    // handed that larger image — so without this clip the shader lays its shape out against
    // the wrong size, and every pane came out oversized by the blur radius on each side.
    private readonly Border _clipper = new() { ClipToBounds = true };
    private readonly Border _sample = new();
    private readonly Border? _fallbackBody;
    private readonly VisualBrush _brush = new()
    {
        ViewboxUnits = BrushMappingMode.Absolute,
        Stretch = Stretch.Fill,
    };
    private readonly BlurEffect _blur = new() { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };
    private readonly LiquidGlassEffect? _effect;

    private UIElement? _child;
    private Rect _viewbox = Rect.Empty;
    private Point _light = new(double.NaN, double.NaN);

    public GlassPanel()
    {
        _sample.Background = _brush;
        _sample.Effect = _blur;
        _clipper.Child = _sample;
        _lens.Children.Add(_clipper);

        if (LiquidGlassEffect.IsAvailable)
        {
            _effect = new LiquidGlassEffect();
            _lens.Effect = _effect;
        }
        else
        {
            // No lens: a frosted pane, clipped to shape, with a plain hairline. Still glass,
            // just not liquid.
            _fallbackBody = new Border
            {
                Margin = new Thickness(Bleed),
                Background = new SolidColorBrush(Color.FromArgb(0x70, 0x0E, 0x0E, 0x10)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
            };
            _lens.Children.Add(_fallbackBody);
        }

        AddVisualChild(_lens);

        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;

        UpdateOptics();
        UpdateBlur();
    }

    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
    public double Frost { get => (double)GetValue(FrostProperty); set => SetValue(FrostProperty, value); }
    public double Refraction { get => (double)GetValue(RefractionProperty); set => SetValue(RefractionProperty, value); }
    public double RimWidth { get => (double)GetValue(RimWidthProperty); set => SetValue(RimWidthProperty, value); }
    public double Dispersion { get => (double)GetValue(DispersionProperty); set => SetValue(DispersionProperty, value); }
    public double BlurRadius { get => (double)GetValue(BlurRadiusProperty); set => SetValue(BlurRadiusProperty, value); }
    public double Highlight { get => (double)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }

    public UIElement? Child
    {
        get => _child;
        set
        {
            if (ReferenceEquals(_child, value)) return;

            if (_child is not null)
            {
                RemoveVisualChild(_child);
                RemoveLogicalChild(_child);
            }

            _child = value;

            if (_child is not null)
            {
                AddLogicalChild(_child);
                AddVisualChild(_child);
            }

            InvalidateMeasure();
        }
    }

    protected override IEnumerator LogicalChildren =>
        _child is null ? Enumerable.Empty<object>().GetEnumerator() : new[] { _child }.GetEnumerator();

    protected override int VisualChildrenCount => _child is null ? 1 : 2;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _lens,
        1 when _child is not null => _child,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    protected override Size MeasureOverride(Size available)
    {
        Thickness pad = Padding;
        double padWidth = pad.Left + pad.Right;
        double padHeight = pad.Top + pad.Bottom;

        if (_child is null) return new Size(padWidth, padHeight);

        _child.Measure(new Size(
            Math.Max(0, available.Width - padWidth),
            Math.Max(0, available.Height - padHeight)));

        return new Size(_child.DesiredSize.Width + padWidth, _child.DesiredSize.Height + padHeight);
    }

    protected override Size ArrangeOverride(Size final)
    {
        // The lens deliberately overhangs the pane on every side; nothing here clips it.
        var lensSize = new Size(final.Width + 2 * Bleed, final.Height + 2 * Bleed);
        _lens.Measure(lensSize);
        _lens.Arrange(new Rect(new Point(-Bleed, -Bleed), lensSize));

        Thickness pad = Padding;
        _child?.Arrange(new Rect(
            pad.Left, pad.Top,
            Math.Max(0, final.Width - pad.Left - pad.Right),
            Math.Max(0, final.Height - pad.Top - pad.Bottom)));

        UpdateShape(final);
        return final;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        // Invisible, but it gives the pane a hit-test area in the shape of the glass.
        double radius = EffectiveRadius(RenderSize);
        drawing.DrawRoundedRectangle(Brushes.Transparent, null, new Rect(RenderSize), radius, radius);
    }

    private double EffectiveRadius(Size size) =>
        Math.Max(0, Math.Min(CornerRadius, Math.Min(size.Width, size.Height) / 2));

    private void OnRendering(object? sender, EventArgs e)
    {
        if (GetScene(this) is not { } scene || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        if (!ReferenceEquals(_brush.Visual, scene))
            _brush.Visual = scene;

        Rect viewbox;
        Point light;

        try
        {
            // Everything that moves the pane — layout, a hover scale, a sheet sliding up —
            // lands in this transform, so polling it each frame is simpler and more reliable
            // than subscribing to each thing that might.
            GeneralTransform toScene = _lens.TransformToVisual(scene);
            viewbox = toScene.TransformBounds(new Rect(_lens.RenderSize));

            if (toScene.Inverse is not { } fromScene) return;
            light = fromScene.Transform(LightPosition);
        }
        catch (InvalidOperationException)
        {
            // Not yet connected to the scene's tree.
            return;
        }

        if (viewbox != _viewbox)
        {
            _viewbox = viewbox;
            _brush.Viewbox = viewbox;
        }

        if (light != _light)
        {
            _light = light;
            UpdateLight();
        }
    }

    private void UpdateShape(Size size)
    {
        double radius = EffectiveRadius(size);

        if (_effect is not null)
            _effect.Geometry = new Point4D(size.Width + 2 * Bleed, size.Height + 2 * Bleed, Bleed, radius);

        if (_fallbackBody is not null)
        {
            _fallbackBody.CornerRadius = new CornerRadius(radius);
            _sample.Clip = new RectangleGeometry(new Rect(Bleed, Bleed, size.Width, size.Height), radius, radius);
        }
    }

    private void UpdateOptics()
    {
        if (_effect is not null)
            _effect.Optics = new Point4D(Refraction, RimWidth, Dispersion, Frost);
    }

    private void UpdateLight()
    {
        if (_effect is not null && !double.IsNaN(_light.X))
            _effect.Light = new Point4D(_light.X, _light.Y, 1.0, Highlight);
    }

    private void UpdateBlur() => _blur.Radius = BlurRadius;

    private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((GlassPanel)d).InvalidateArrange();

    private static void OnOpticsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((GlassPanel)d).UpdateOptics();

    private static void OnBlurChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((GlassPanel)d).UpdateBlur();

    private static void OnLightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((GlassPanel)d).UpdateLight();
}
