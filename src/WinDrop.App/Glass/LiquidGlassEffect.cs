using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace WinDrop.App.Glass;

/// <summary>The lens shader. Constants mirror the registers declared in LiquidGlass.hlsl.</summary>
public sealed class LiquidGlassEffect : ShaderEffect
{
    private static PixelShader? _shader;
    private static bool _attempted;

    /// <summary>Why the glass fell back to plain frosted panels, if it did.</summary>
    public static string? UnavailableReason { get; private set; }

    public static bool IsAvailable
    {
        get
        {
            if (_attempted) return _shader is not null;
            _attempted = true;

            // ps_3_0 runs only on the GPU. Under WPF's software renderer — a remote desktop
            // session, a VM without acceleration — the effect would be dropped silently and
            // the panel would draw as a blurred square with its bleed showing.
            if (!RenderCapability.IsPixelShaderVersionSupported(3, 0))
            {
                UnavailableReason = "this display has no hardware pixel shader 3.0 support";
                return false;
            }

            _shader = ShaderCompiler.Compile("WinDrop.App.Glass.LiquidGlass.hlsl", out string? error);
            UnavailableReason = error;

            return _shader is not null;
        }
    }

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(nameof(Input), typeof(LiquidGlassEffect), 0);

    public static readonly DependencyProperty GeometryProperty = DependencyProperty.Register(
        nameof(Geometry), typeof(Point4D), typeof(LiquidGlassEffect),
        new UIPropertyMetadata(default(Point4D), PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty OpticsProperty = DependencyProperty.Register(
        nameof(Optics), typeof(Point4D), typeof(LiquidGlassEffect),
        new UIPropertyMetadata(default(Point4D), PixelShaderConstantCallback(1)));

    public static readonly DependencyProperty LightProperty = DependencyProperty.Register(
        nameof(Light), typeof(Point4D), typeof(LiquidGlassEffect),
        new UIPropertyMetadata(default(Point4D), PixelShaderConstantCallback(2)));

    public LiquidGlassEffect()
    {
        PixelShader = _shader;

        UpdateShaderValue(InputProperty);
        UpdateShaderValue(GeometryProperty);
        UpdateShaderValue(OpticsProperty);
        UpdateShaderValue(LightProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    /// <summary>Input width, input height, bleed, corner radius.</summary>
    public Point4D Geometry
    {
        get => (Point4D)GetValue(GeometryProperty);
        set => SetValue(GeometryProperty, value);
    }

    /// <summary>Refraction depth, rim width, dispersion, frost.</summary>
    public Point4D Optics
    {
        get => (Point4D)GetValue(OpticsProperty);
        set => SetValue(OpticsProperty, value);
    }

    /// <summary>Light x, y in input-local DIPs, specular strength, highlight.</summary>
    public Point4D Light
    {
        get => (Point4D)GetValue(LightProperty);
        set => SetValue(LightProperty, value);
    }
}
