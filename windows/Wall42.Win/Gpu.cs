using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Wall42;

/// GPU resources shared by every monitor: one device, shaders compiled once, pipelines built once, and ONE
/// copy of the particle/link buffers (uploaded once per simulation step, drawn by every monitor).
sealed class Gpu : IDisposable
{
    public readonly ID3D11Device Device;
    public readonly ID3D11DeviceContext Ctx;
    public readonly IDXGIFactory2 Factory;
    readonly ID3D11VertexShader bgVs, particleVs, lineVs;
    readonly ID3D11PixelShader bgPs, particlePs, linePs;
    readonly ID3D11BlendState additive, normal;
    readonly ID3D11RasterizerState raster;
    readonly ID3D11Buffer cb;
    ID3D11Buffer? particleBuf, linkBuf;
    ID3D11ShaderResourceView? particleSrv, linkSrv;
    int particleCap, linkCap;
    int uploadedStep = -1;
    int uploadedParticles, uploadedLinks;
    public string AdapterName { get; }

    public Gpu()
    {
        // BGRA support: the swap chains are B8G8R8A8 like Metal's bgra8Unorm
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
            out ID3D11Device dev, out ID3D11DeviceContext ctx).CheckError();
        Device = dev!; Ctx = ctx!;
        using (var dxgiDev = Device.QueryInterface<IDXGIDevice1>())
        {
            dxgiDev.MaximumFrameLatency = 1;          // a wallpaper never needs frames queued ahead
            using var adapter = dxgiDev.GetAdapter();
            AdapterName = adapter.Description.Description;
        }
        Factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();

        // compiled at startup from the embedded source, like the Mac compiles its Metal source at runtime
        string src;
        using (var s = typeof(Gpu).Assembly.GetManifestResourceStream("Shaders.hlsl")!)
        using (var r = new StreamReader(s)) src = r.ReadToEnd();
        byte[] Compile(string entry, string profile)
        {
            var hr = Compiler.Compile(src, entry, "Shaders.hlsl", profile, out var blob, out var err);
            if (hr.Failure || blob == null)
                throw new InvalidOperationException($"shader {entry}: {err?.AsString() ?? hr.ToString()}");
            var bytes = blob.AsBytes();
            blob.Dispose(); err?.Dispose();
            return bytes;
        }
        bgVs = Device.CreateVertexShader(Compile("bg_vs", "vs_4_0"));
        bgPs = Device.CreatePixelShader(Compile("bg_fs", "ps_4_0"));
        particleVs = Device.CreateVertexShader(Compile("particle_vs", "vs_5_0"));
        particlePs = Device.CreatePixelShader(Compile("particle_fs", "ps_5_0"));
        lineVs = Device.CreateVertexShader(Compile("line_vs", "vs_5_0"));
        linePs = Device.CreatePixelShader(Compile("line_fs", "ps_5_0"));

        // additive: overlaps get brighter (part of the neon look). normal: fragment is premultiplied, no blow-out.
        additive = Device.CreateBlendState(MakeBlend(Blend.One));
        normal = Device.CreateBlendState(MakeBlend(Blend.InverseSourceAlpha));
        raster = Device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { DepthClipEnable = true });
        cb = Device.CreateBuffer(new BufferDescription((uint)Unsafe.SizeOf<Uniforms>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
    }

    static BlendDescription MakeBlend(Blend dest)
    {
        var d = new BlendDescription();
        d.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.One, DestinationBlend = dest, BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One, DestinationBlendAlpha = dest, BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        return d;
    }

    /// Particles and links go up once per simulation step, however many monitors draw them.
    public void Upload(World w)
    {
        if (w.StepCount == uploadedStep && uploadedParticles == w.DrawCount) return;
        uploadedStep = w.StepCount;
        uploadedParticles = Put(w.Particles, ref particleBuf, ref particleSrv, ref particleCap);
        uploadedLinks = Put(w.Links, ref linkBuf, ref linkSrv, ref linkCap);
    }

    unsafe int Put<T>(ReadOnlySpan<T> data, ref ID3D11Buffer? buf, ref ID3D11ShaderResourceView? srv, ref int cap) where T : unmanaged
    {
        if (data.Length == 0) return 0;
        if (buf == null || data.Length > cap)
        {
            srv?.Dispose(); buf?.Dispose();
            cap = data.Length + data.Length / 4 + 64;      // headroom: small count changes don't reallocate
            int stride = sizeof(T);
            buf = Device.CreateBuffer(new BufferDescription((uint)(cap * stride), BindFlags.ShaderResource, ResourceUsage.Dynamic,
                CpuAccessFlags.Write, ResourceOptionFlags.BufferStructured, (uint)stride));
            srv = Device.CreateShaderResourceView(buf);
        }
        var m = Ctx.Map(buf, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        data.CopyTo(new Span<T>((void*)m.DataPointer, data.Length));
        Ctx.Unmap(buf, 0);
        return data.Length;
    }

    /// Background → links → particles, far to near (same order as the Metal renderer).
    public unsafe void Draw(ID3D11RenderTargetView rtv, int width, int height, in Uniforms u, bool normalBlend, bool noDraw)
    {
        Ctx.OMSetRenderTargets(rtv);
        Ctx.RSSetViewport(new Viewport(0, 0, width, height));
        if (noDraw) { Ctx.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 1)); return; }   // framework baseline

        var m = Ctx.Map(cb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        *(Uniforms*)m.DataPointer = u;
        Ctx.Unmap(cb, 0);
        Ctx.RSSetState(raster);
        Ctx.VSSetConstantBuffer(0, cb);
        Ctx.PSSetConstantBuffer(0, cb);

        Ctx.OMSetBlendState(null);
        Ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Ctx.VSSetShader(bgVs); Ctx.PSSetShader(bgPs);
        Ctx.Draw(3, 0);

        var blend = normalBlend ? normal : additive;
        if (uploadedLinks > 0 && linkSrv != null)
        {
            Ctx.OMSetBlendState(blend);
            Ctx.IASetPrimitiveTopology(PrimitiveTopology.LineList);
            Ctx.VSSetShader(lineVs); Ctx.PSSetShader(linePs);
            Ctx.VSSetShaderResource(1, linkSrv);
            Ctx.Draw((uint)uploadedLinks, 0);
        }
        if (uploadedParticles > 0 && particleSrv != null)
        {
            Ctx.OMSetBlendState(blend);
            Ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            Ctx.VSSetShader(particleVs); Ctx.PSSetShader(particlePs);
            Ctx.VSSetShaderResource(0, particleSrv);
            Ctx.DrawInstanced(4, (uint)uploadedParticles, 0, 0);
        }
    }

    /// WALL42_SNAPSHOT: draw one view into an offscreen texture and read it back as RGBA rows.
    public unsafe byte[] RenderOffscreen(World w, int width, int height, in Uniforms u, bool normalBlend)
    {
        using var tex = Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget));
        using var rtv = Device.CreateRenderTargetView(tex);
        Upload(w);
        Draw(rtv, width, height, u, normalBlend, false);
        using var staging = Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        Ctx.CopyResource(staging, tex);
        var m = Ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            var row = new ReadOnlySpan<byte>((byte*)m.DataPointer + (long)y * m.RowPitch, width * 4);
            for (int x = 0; x < width; x++)
            {
                int s = x * 4, d = (y * width + x) * 4;
                rgba[d] = row[s + 2]; rgba[d + 1] = row[s + 1]; rgba[d + 2] = row[s]; rgba[d + 3] = 255;   // BGRA → RGBA
            }
        }
        Ctx.Unmap(staging, 0);
        return rgba;
    }

    public void Dispose()
    {
        particleSrv?.Dispose(); particleBuf?.Dispose(); linkSrv?.Dispose(); linkBuf?.Dispose();
        cb.Dispose(); raster.Dispose(); additive.Dispose(); normal.Dispose();
        bgVs.Dispose(); bgPs.Dispose(); particleVs.Dispose(); particlePs.Dispose(); lineVs.Dispose(); linePs.Dispose();
        Ctx.ClearState(); Ctx.Flush(); Ctx.Dispose(); Factory.Dispose(); Device.Dispose();
    }
}

/// Minimal PNG writer (RGBA8, zlib via System.IO.Compression) — no System.Drawing/WIC dependency for a debug output.
static class Png
{
    static readonly uint[] crcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static void Write(string path, byte[] rgba, int w, int h)
    {
        using var fs = File.Create(path);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h);
        ihdr[8] = 8; ihdr[9] = 6;                          // 8-bit RGBA
        Chunk(fs, "IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(raw, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgba, y * w * 4, w * 4); }
        Chunk(fs, "IDAT", raw.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    static void BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, (uint)data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t); s.Write(data);
        uint c = 0xFFFFFFFF;
        foreach (var b in t) c = crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        var crc = new byte[4]; BE(crc, 0, c ^ 0xFFFFFFFF); s.Write(crc);
    }
}
