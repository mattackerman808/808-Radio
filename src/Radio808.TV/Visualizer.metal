#include <metal_stdlib>
using namespace metal;

// 808 Radio's screen saver: an old-school music visualizer. Every frame is the last one warped (zoomed, turned,
// swirled by the music), faded a little, with the waveform drawn over it in a drifting hue: the feedback loop that
// made MilkDrop. The preset picks the warp and the shape.

struct U
{
    float2 res;      // drawable size
    float  time;
    float  dt;
    float  bass, mid, treb;   // 0..1-ish band levels
    float  beat;     // 1 on a beat, decaying
    float  hue;      // 0..1, drifting
    int    preset;
    float  blend;    // 0..1 while a new preset fades in (unused for now)
    float  pad;
};

struct VOut { float4 pos [[position]]; float2 uv; };

vertex VOut vmain(uint id [[vertex_id]])
{
    float2 p[3] = { float2(-1, -1), float2(3, -1), float2(-1, 3) };
    VOut o;
    o.pos = float4(p[id], 0, 1);
    o.uv = float2(p[id].x * 0.5 + 0.5, 1 - (p[id].y * 0.5 + 0.5));
    return o;
}

static float3 hsv(float h, float s, float v)
{
    float3 k = float3(1, 2.0 / 3.0, 1.0 / 3.0);
    float3 p = abs(fract(float3(h) + k) * 6 - 3);
    return v * mix(float3(1), clamp(p - 1, 0.0, 1.0), s);
}

static float2 rot(float2 p, float a) { float c = cos(a), s = sin(a); return float2(c * p.x - s * p.y, s * p.x + c * p.y); }

// the previous frame, warped: where this pixel reads its history from
static float2 warp(float2 c, constant U& u)
{
    float r = length(c), a = atan2(c.y, c.x);
    float t = u.time;
    switch (u.preset)
    {
    case 0:   // the tunnel: zoom in, slow turn, breathing with the bass
        return rot(c * (1.0 - 0.012 - 0.03 * u.bass), 0.004 + 0.02 * u.mid);
    case 1:   // the swirl: turn faster toward the middle, drift outward
        return rot(c * (1.0 + 0.006), (0.35 + 0.8 * u.treb) * exp(-r * 3.0) * 0.12 + 0.002);
    case 2:   // the ripple: waves across the picture, driven by the bass
        return c * (1.0 - 0.008) + float2(sin(c.y * 9.0 + t * 1.3), cos(c.x * 7.0 - t * 1.1)) * (0.004 + 0.012 * u.bass);
    case 3:   // the kaleidoscope: mirrored sectors, turning
        {
            float sectors = 6.0;
            float sa = fmod(a + 3.14159265 + t * 0.05, 2 * 3.14159265 / sectors);
            sa = abs(sa - 3.14159265 / sectors);
            float2 m = float2(cos(sa), sin(sa)) * r;
            return rot(m * (1.0 - 0.006 - 0.008 * u.bass), 0.004);
        }
    case 4:   // the fall: everything slides down and shrinks, like rain on the glass
        return float2(c.x * (1.0 - 0.01), c.y * (1.0 - 0.01) - 0.004 - 0.01 * u.bass) + float2(sin(c.y * 20.0 + t * 2.0) * 0.002, 0);
    default:  // the bloom: zoom out from the centre, turning with the mids
        return rot(c * (1.0 + 0.02 + 0.03 * u.bass), -0.006 - 0.03 * u.mid);
    }
}

fragment float4 fwarp(VOut in [[stage_in]], constant U& u [[buffer(0)]], constant float* audio [[buffer(1)]],
                      texture2d<float> prev [[texture(0)]])
{
    constexpr sampler smp(address::clamp_to_edge, filter::linear);
    float aspect = u.res.x / u.res.y;
    float2 c = (in.uv - 0.5) * float2(aspect, 1.0);
    // the motion, slowed to under half: the same shapes, a gentler ride (the kaleidoscope remaps rather than moves)
    float2 w = u.preset == 3 ? warp(c, u) : mix(c, warp(c, u), 0.42);
    float2 uv2 = w / float2(aspect, 1.0) + 0.5;
    float4 old = prev.sample(smp, uv2);
    // fade, and drift the hue of what's left
    float3 col = old.rgb * (0.94 + 0.03 * u.bass) - 0.005;
    col = max(col, 0.0);
    col = mix(col, col.gbr, 0.015);

    float r = length(c), a = atan2(c.y, c.x);
    const float pi = 3.14159265;
    float3 ink = hsv(u.hue + a / (2 * pi) * 0.25, 0.85, 1.0);
    float glow = 0;

    if (u.preset == 2 || u.preset == 4)
    {
        // the waveform across the screen, mirrored top and bottom
        float x = clamp(in.uv.x, 0.0, 0.999);
        float s = audio[int(x * 511.0)];
        float y = 0.5 + s * (0.18 + 0.1 * u.bass);
        float d = min(abs(in.uv.y - y), abs(in.uv.y - (1.0 - y)));
        glow = exp(-d * d * 9000.0) * (0.45 + 0.6 * u.beat);
        ink = hsv(u.hue + in.uv.x * 0.3, 0.8, 1.0);
    }
    else
    {
        // the waveform around a ring that breathes with the bass
        float idx = fract(a / (2 * pi) + 0.5 + u.time * 0.008) * 511.0;
        float s = audio[int(idx)];
        float ring = 0.2 + 0.1 * u.bass + s * (0.1 + 0.08 * u.treb);
        float d = abs(r - ring);
        glow = exp(-d * d * 7000.0) * (0.45 + 0.7 * u.beat);
        // and a second, fainter ring on the beat
        float d2 = abs(r - ring * (1.6 + 0.5 * u.beat));
        glow += exp(-d2 * d2 * 4000.0) * 0.35 * u.beat;
    }
    col += ink * glow;
    // a flash from the middle on a strong beat
    col += hsv(u.hue + 0.5, 0.6, 1.0) * exp(-r * 6.0) * u.beat * u.beat * 0.25;
    return float4(min(col, 1.0), 1);
}

fragment float4 fshow(VOut in [[stage_in]], texture2d<float> tex [[texture(0)]])
{
    constexpr sampler smp(address::clamp_to_edge, filter::linear);
    float3 c = tex.sample(smp, in.uv).rgb;
    // a soft bloom: the picture plus a blurred, dimmer copy of itself
    float3 b = 0;
    float2 px = 2.5 / float2(tex.get_width(), tex.get_height());
    for (int i = -2; i <= 2; i++) for (int j = -2; j <= 2; j++) b += tex.sample(smp, in.uv + float2(i, j) * px).rgb;
    c = c + b / 25.0 * 0.35;
    // dark edges
    float2 d = in.uv - 0.5;
    c *= 1.0 - dot(d, d) * 0.9;
    return float4(c, 1);
}
