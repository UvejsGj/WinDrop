// Liquid Glass: a lens drawn over a blurred copy of whatever sits behind it.
//
// The input is that blurred copy, larger than the glass by Geometry.z on every side. The
// margin matters twice over: the rim bends light in from outside the shape, so there have
// to be real pixels out there to sample, and the shadow needs somewhere to fall.
//
// Everything is worked in DIPs. uv is converted once on the way in and back only to sample.

sampler2D Input : register(s0);

float4 Geometry : register(c0);  // input width, input height, bleed, corner radius
float4 Optics   : register(c1);  // refraction depth, rim width, dispersion, frost
float4 Light    : register(c2);  // light x, y (input-local), specular strength, highlight

float RoundedBox(float2 p, float2 halfSize, float radius)
{
    float2 q = abs(p) - halfSize + radius;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 size = Geometry.xy;
    float2 halfSize = size * 0.5 - Geometry.z;
    float radius = min(Geometry.w, min(halfSize.x, halfSize.y));

    float2 position = uv * size;
    float2 p = position - size * 0.5;

    // Signed distance to the glass edge: negative inside, zero on the rim.
    float d = RoundedBox(p, halfSize, radius);
    float coverage = saturate(0.5 - d);

    // The shadow is the same shape, dropped a little and softened across the bleed.
    float shadowDistance = RoundedBox(p - float2(0.0, 6.0), halfSize, radius);
    float shadow = 0.42 * (1.0 - smoothstep(-12.0, Geometry.z * 0.9, shadowDistance));

    // The surface normal is the gradient of the distance field.
    float2 gradient = float2(
        RoundedBox(p + float2(0.5, 0.0), halfSize, radius) - RoundedBox(p - float2(0.5, 0.0), halfSize, radius),
        RoundedBox(p + float2(0.0, 0.5), halfSize, radius) - RoundedBox(p - float2(0.0, 0.5), halfSize, radius));
    float2 normal = gradient / max(length(gradient), 0.0001);

    // Height profile of a thick slab with rounded shoulders: flat across the middle, rolling
    // off toward the edge. Light bends in proportion to that slope, so the middle shows the
    // scene undistorted and the rim pulls in what lies just beyond the edge.
    float edge = saturate(1.0 + d / Optics.y);
    float edge3 = edge * edge * edge;
    float2 offset = normal * edge3 * Optics.x;

    // Dispersion: each channel bends by a slightly different amount, as real glass does.
    float2 texel = 1.0 / size;
    float red   = tex2D(Input, uv + offset * (1.0 + Optics.z) * texel).r;
    float green = tex2D(Input, uv + offset * texel).g;
    float blue  = tex2D(Input, uv + offset * (1.0 - Optics.z) * texel).b;
    float3 color = float3(red, green, blue);

    // Frost pulls the body toward a dark neutral, so text laid on it stays legible.
    color = lerp(color, float3(0.055, 0.055, 0.06), Optics.w);

    // Fresnel: at a glancing angle the rim reflects more of the room than it transmits.
    color += edge3 * 0.09;

    // Specular: a hairline on the rim, brightest on the side that faces the light, with a
    // weaker kick on the far side where light exits the slab.
    float2 toLight = normalize(Light.xy - position + 0.0001);
    float facing = dot(normal, toLight);
    float hairline = 1.0 - smoothstep(0.0, 1.6, abs(d + 1.0));
    color += hairline * (0.16 + 0.84 * saturate(facing)) * Light.z;
    color += hairline * saturate(-facing) * Light.z * 0.3;

    // A broad sheen just inside the lit shoulder.
    color += edge3 * edge3 * saturate(facing) * 0.18 * Light.z;

    // Hover and drag feedback: the whole pane lifts slightly.
    color += Light.w * 0.07;

    // WPF effects work in premultiplied alpha. The shadow is black, so it adds only alpha.
    float alpha = coverage + shadow * (1.0 - coverage);
    return float4(color * coverage, alpha);
}
