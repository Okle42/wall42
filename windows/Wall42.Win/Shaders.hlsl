// HLSL port of the Metal shaders in Sources/Renderer.swift. Same maths, same names, line for line.
// Keep this file pure ASCII: Vortice passes the source to D3DCompile with a length that non-ASCII bytes throw off.
// No vertex buffers or input layouts: everything is SV_VertexID / SV_InstanceID into structured buffers,
// exactly like Metal's [[vertex_id]] / [[instance_id]] into `constant Particle *`.

cbuffer Uniforms : register(b0)   // packed so nothing straddles 16 bytes; mirrors World.cs `Uniforms` (176 bytes)
{
    float2 viewport;  float time;  float brightness;
    float4 colorA;
    float4 colorB;
    float4 bgCenter;
    float4 bgEdge;
    float  bgRadius;  float linkBoost;  float breathSpeed;  float twinkleAmount;
    float  bokehDimming;  float pulseSpeed;  float pulseStrength;  float pulseWidth;
    float  activity;  float glow;  float insight;  float softness;
    float4 linkColor;
    float2 viewOrigin;  float2 viewSize;
    float2 worldSize;  float pxScale;  float bgMode;
};

// Metal's smoothstep accepts edge0 > edge1 (used below as smoothstep(0.5, 0.0, d)); HLSL leaves that undefined.
float sstep(float e0, float e1, float x) { float t = saturate((x - e0) / (e1 - e0)); return t * t * (3.0 - 2.0 * t); }

// world coordinates (points) -> this monitor's NDC
float2 world_to_ndc(float2 p)
{
    float2 ndc = ((p - viewOrigin) / viewSize) * 2.0 - 1.0;
    ndc.y = -ndc.y;
    return ndc;
}

// -- background: radial centre->edge falloff per monitor (lighting + vignette), or a whole-world vertical sky --
float4 bg_vs(uint vid : SV_VertexID) : SV_Position
{
    // one triangle over the whole screen: one vertex fewer than a quad, and no diagonal seam
    float2 p = vid == 0 ? float2(-1, -1) : vid == 1 ? float2(3, -1) : float2(-1, 3);
    return float4(p, 0, 1);
}

float4 bg_fs(float4 pos : SV_Position) : SV_Target
{
    float3 col;
    if (bgMode > 0.5)
    {
        // vertical sky in world coordinates: two monitors side by side are one sky, no seam
        float wy = viewOrigin.y + pos.y / max(pxScale, 1.0);
        float t = saturate(wy / max(worldSize.y, 1.0));
        t = pow(max(t, 1e-6), max(0.05, bgRadius));
        col = lerp(bgEdge.rgb, bgCenter.rgb, t);
    }
    else
    {
        float2 uv = pos.xy / viewport;
        float2 c = uv - 0.5;
        c.x *= viewport.x / viewport.y;          // aspect fix, or the gradient becomes an ellipse
        float r = length(c);
        float t = sstep(0.0, bgRadius, r);
        t = t * t;                               // really black at the edges, long soft transition
        col = lerp(bgCenter.rgb, bgEdge.rgb, t);
    }
    // 8-bit dark gradients band into rings: half a step of noise breaks them up
    float n = frac(sin(dot(pos.xy, float2(12.9898, 78.233))) * 43758.5453);
    col += (n - 0.5) / 255.0;
    return float4(col, 1.0);
}

// -- particles --
struct Particle { float2 pos; float2 vel; float size; float phase; float colorMix; float depth; float twinkle; float boost; float fade; };
StructuredBuffer<Particle> particles : register(t0);

struct PointOut
{
    float4 position : SV_Position;
    float2 uv       : TEXCOORD0;
    float  alpha    : TEXCOORD1;
    float  colorMix : TEXCOORD2;
    float  depth    : TEXCOORD3;
    float  ring     : TEXCOORD4;   // > 0: Claude session point, one thin outer ring (value = ring brightness)
};

// Instanced quads, not point sprites: a point is culled as soon as its centre leaves the screen, so a big
// bokeh crossing a monitor seam would vanish; a quad is clipped geometrically, half on each monitor.
PointOut particle_vs(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    Particle p = particles[iid];
    PointOut o;
    bool session = p.depth < -0.5;               // quad 2.6x larger to leave room for the ring; core size unchanged
    o.ring = session ? (0.45 + min(p.boost, 1.5) * 0.5) : 0.0;
    p.depth = max(p.depth, 0.0);
    float2 corner = float2((vid & 1) ? 1.0 : -1.0, (vid & 2) ? 1.0 : -1.0);
    float psize = p.size * (session ? 2.6 : (1.0 + p.boost * 1.6)) * pxScale;   // pixel diameter
    float2 ndc = world_to_ndc(p.pos) + corner * psize / viewport;
    o.position = float4(ndc, 0.0, 1.0);
    o.uv = corner * 0.5 + 0.5;
    o.colorMix = p.colorMix;
    o.depth = p.depth;
    // twinkle: three incommensurate frequencies. One sine is a mechanical breath; real starlight is
    // irregular atmospheric flicker with uneven peaks.
    float t = time * breathSpeed * p.twinkle;
    float ph = p.phase;
    float w = sin(t + ph) * 0.55 + sin(t * 2.31 + ph * 1.7) * 0.28 + sin(t * 4.67 + ph * 2.9) * 0.17;
    // small stars flicker visibly, big nodes stay steady (dim point sources suffer more from turbulence)
    float sizeFactor = clamp(1.0 - (p.size - 3.0) * 0.045, 0.30, 1.0);
    float amp = twinkleAmount * sizeFactor;
    float breathe = 1.0 - amp * 0.5 + amp * 0.5 * w;
    o.alpha = lerp(breathe, breathe * bokehDimming, p.depth) * brightness
            * (1.0 + p.boost * 1.2) * (1.0 + insight * 0.9) * p.fade;
    return o;
}

float4 particle_fs(PointOut i) : SV_Target
{
    float d = length(i.uv - 0.5);
    if (d > 0.5 || i.alpha <= 0.0) discard;
    float ringA = 0.0;
    if (i.ring > 0.0)
    {
        // outer ring: a thin circle in the link colour, brighter when busy; the core as a normal node
        float rd = (d - 0.40) / 0.028;
        ringA = exp(-rd * rd) * i.ring;
        d = d * 2.6;
    }
    // falloff follows depth: far = steep = sharp point; near = gentle = soft disc (fake defocus, same cost).
    // glow = 0 falls off faster still: a solid soft dot instead of a halo.
    float sharpness = lerp(lerp(4.2, 2.4, glow), 0.85, max(i.depth, softness));
    float halo = pow(max(sstep(0.5, 0.0, d), 1e-6), sharpness);
    // the over-exposed white core only with glow: the main source of the "neon" look
    float core = pow(max(sstep(0.30, 0.0, d), 1e-6), 1.4) * (1.0 - i.depth * 0.88) * glow;
    if (d > 0.5) { halo = 0.0; core = 0.0; }
    float3 col = lerp(colorA.rgb, colorB.rgb, i.colorMix);
    float3 rgb = col * halo * lerp(1.0, 1.7, glow) + float3(1, 1, 1) * core * 1.15;
    float a = saturate(halo + core) * i.alpha;
    float3 ringRGB = linkColor.rgb * 1.6 * ringA * brightness;
    return float4(rgb * i.alpha + ringRGB, max(a, ringA * 0.8));
}

// -- links --
struct LinkVertex { float2 pos; float alpha; float colorMix; float t; float seed; float age; };
StructuredBuffer<LinkVertex> linkVerts : register(t1);

struct LineOut
{
    float4 position : SV_Position;
    float  alpha    : TEXCOORD0;
    float  colorMix : TEXCOORD1;
    float  t        : TEXCOORD2;
    float  seed     : TEXCOORD3;   // proximity: pulse phase; < 0: use age
    float  age      : TEXCOORD4;
};

LineOut line_vs(uint vid : SV_VertexID)
{
    LinkVertex v = linkVerts[vid];
    LineOut o;
    o.position = float4(world_to_ndc(v.pos), 0.0, 1.0);
    o.alpha = v.alpha * brightness * (1.0 + insight * 1.4);
    o.colorMix = v.colorMix;
    o.t = v.t;
    o.seed = v.seed;
    o.age = v.age;
    return o;
}

float4 line_fs(LineOut i) : SV_Target
{
    // links are one colour: different end colours make a line look like it is flickering
    float3 col = linkColor.rgb;
    float base = i.alpha * linkBoost;
    // a pulse running along the line. t is already interpolated, so no extra geometry: one exp per pixel.
    // traffic/attention (seed < 0): the pulse position is the link's age, so
    // "created -> transfer -> gone" is exactly one delivery.
    float head = i.seed < 0.0 ? i.age : frac(time * pulseSpeed * (0.6 + i.seed * 0.8) + i.seed);
    float d = abs(i.t - head);
    d = min(d, 1.0 - d);                         // wrap the pulse from the end back to the start
    float w = max(0.0008, pulseWidth);
    float pulse = exp(-(d * d) / w) * pulseStrength;
    // busier = brighter pulse, and the line itself a little brighter
    float a = base * (1.0 + activity * 0.5) + pulse;
    float3 rgb = col * a + float3(1, 1, 1) * pulse * 0.35 * activity * glow;
    return float4(rgb, a);
}
