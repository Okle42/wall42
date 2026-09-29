using static Wall42.UiNative;

namespace Wall42;

/// A 32-bit top-down DIB with a memory DC: anti-aliased rounded rectangles / circles / capsules computed
/// per pixel (GDI has no anti-aliasing and GDI+ would pull in System.Drawing), text through GDI (ClearType).
/// Shapes write the pixels directly, text goes through the DC: call Flush() between the two.
sealed unsafe class Canvas : IDisposable
{
    public readonly int W, H;
    public readonly IntPtr Dc;
    readonly IntPtr bmp, old;
    readonly uint* px;

    public Canvas(int w, int h)
    {
        W = Math.Max(1, w); H = Math.Max(1, h);
        var bi = new BITMAPINFOHEADER { biSize = sizeof(BITMAPINFOHEADER), biWidth = W, biHeight = -H, biPlanes = 1, biBitCount = 32 };
        bmp = CreateDIBSection(IntPtr.Zero, ref bi, 0, out var bits, IntPtr.Zero, 0);
        if (bmp == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection");
        px = (uint*)bits;
        Dc = CreateCompatibleDC(IntPtr.Zero);
        old = SelectObject(Dc, bmp);
        SetBkMode(Dc, 1);                               // TRANSPARENT
    }

    public uint* Pixels => px;

    public void Clear(uint rgb)
    {
        GdiFlush();
        uint v = 0xFF000000 | rgb;
        new Span<uint>(px, W * H).Fill(v);
    }

    /// Rounded rectangle (r = corner radius), straight colour 0xRRGGBB, opacity a.
    public void RoundRect(float x, float y, float w, float h, float r, uint rgb, float a = 1)
    {
        if (w <= 0 || h <= 0) return;
        r = Math.Min(r, Math.Min(w, h) / 2);
        float cx = x + w / 2, cy = y + h / 2, hx = w / 2 - r, hy = h / 2 - r;
        Fill(x, y, w, h, rgb, a, (px, py) =>
        {
            float qx = Math.Abs(px - cx) - hx, qy = Math.Abs(py - cy) - hy;
            float ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
            return MathF.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - r;
        });
    }

    public void Circle(float cx, float cy, float r, uint rgb, float a = 1) => RoundRect(cx - r, cy - r, 2 * r, 2 * r, r, rgb, a);

    /// A line with round caps, width w.
    public void Capsule(float x0, float y0, float x1, float y1, float w, uint rgb, float a = 1)
    {
        float r = w / 2, dx = x1 - x0, dy = y1 - y0, len2 = Math.Max(1e-6f, dx * dx + dy * dy);
        float minx = Math.Min(x0, x1) - r, miny = Math.Min(y0, y1) - r;
        Fill(minx, miny, Math.Abs(dx) + w, Math.Abs(dy) + w, rgb, a, (px, py) =>
        {
            float t = Math.Clamp(((px - x0) * dx + (py - y0) * dy) / len2, 0, 1);
            float ex = px - (x0 + t * dx), ey = py - (y0 + t * dy);
            return MathF.Sqrt(ex * ex + ey * ey) - r;
        });
    }

    void Fill(float x, float y, float w, float h, uint rgb, float a, Func<float, float, float> sdf)
    {
        int x0 = Math.Max(0, (int)MathF.Floor(x - 1)), y0 = Math.Max(0, (int)MathF.Floor(y - 1));
        int x1 = Math.Min(W, (int)MathF.Ceiling(x + w + 1)), y1 = Math.Min(H, (int)MathF.Ceiling(y + h + 1));
        if (x0 >= x1 || y0 >= y1) return;
        GdiFlush();
        uint sr = (rgb >> 16) & 0xFF, sg = (rgb >> 8) & 0xFF, sb = rgb & 0xFF;
        for (int j = y0; j < y1; j++)
        {
            uint* row = px + j * W;
            for (int i = x0; i < x1; i++)
            {
                float cov = Math.Clamp(0.5f - sdf(i + 0.5f, j + 0.5f), 0, 1) * a;
                if (cov <= 0.002f) continue;
                uint d = row[i];
                uint k = (uint)(cov * 256);
                uint dr = (d >> 16) & 0xFF, dg = (d >> 8) & 0xFF, db = d & 0xFF;
                dr += (uint)(((int)sr - (int)dr) * (int)k >> 8);
                dg += (uint)(((int)sg - (int)dg) * (int)k >> 8);
                db += (uint)(((int)sb - (int)db) * (int)k >> 8);
                row[i] = 0xFF000000 | (dr << 16) | (dg << 8) | db;
            }
        }
    }

    public void Text(string s, IntPtr font, uint rgb, int x, int y, int w, int h, uint align = DT_LEFT)
    {
        SelectObject(Dc, font);
        SetTextColor(Dc, Rgb2Ref(rgb));
        var r = new Native.RECT(x, y, x + w, y + h);
        DrawTextW(Dc, s, s.Length, ref r, align | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
    }

    public void Flush() => GdiFlush();

    public void BlitTo(IntPtr dst, int x, int y) { GdiFlush(); BitBlt(dst, x, y, W, H, Dc, 0, 0, SRCCOPY); }

    public static uint Rgb2Ref(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
    public static uint Ref2Rgb(uint cref) => Rgb2Ref(cref);

    public static uint Mix(uint a, uint b, float t)
    {
        int Ch(int s) => (int)(((a >> s) & 0xFF) + (((int)((b >> s) & 0xFF) - (int)((a >> s) & 0xFF)) * t));
        return (uint)((Ch(16) << 16) | (Ch(8) << 8) | Ch(0));
    }

    public static uint ParseHex(string? s, uint fallback = 0)
    {
        var t = (s ?? "").Trim().TrimStart('#');
        return t.Length == 6 && uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : fallback;
    }

    public static string Hex(uint rgb) => $"#{rgb & 0xFFFFFF:X6}";

    public void Dispose()
    {
        SelectObject(Dc, old);
        DeleteDC(Dc);
        DeleteObject(bmp);
    }
}

/// The tray / window icon: seven particles in a hexagon joined by faint links (the Mac uses the SF Symbol
/// circle.hexagongrid), cyan → pink like the default colours. Paused: the same in grey.
static unsafe class Icons
{
    public static IntPtr Make(int size, bool paused)
    {
        var px = new uint[size * size];
        float s = size, cx = s / 2, cy = s / 2, ring = s * 0.34f, dot = s * 0.115f, center = s * 0.14f;
        var pts = new List<(float x, float y, float r)> { (cx, cy, center) };
        for (int i = 0; i < 6; i++)
        {
            double a = Math.PI / 6 + i * Math.PI / 3;
            pts.Add((cx + ring * (float)Math.Cos(a), cy + ring * (float)Math.Sin(a), dot));
        }
        uint A = 0x1ADBF5, B = 0xFC3D99;
        var cov = new float[size * size];
        var col = new uint[size * size];
        void Put(Func<float, float, float> sdf, Func<float, float, uint> color, float alpha)
        {
            for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                float k = Math.Clamp(0.5f - sdf(i + 0.5f, j + 0.5f), 0, 1) * alpha;
                if (k <= 0) continue;
                int n = j * size + i;
                // "over" in straight alpha
                float a0 = cov[n], a1 = k + a0 * (1 - k);
                uint src = color(i + 0.5f, j + 0.5f);
                col[n] = a1 <= 0 ? src : Canvas.Mix(col[n], src, k / a1);
                cov[n] = a1;
            }
        }
        uint ColorAt(float x, float y) { var t = Math.Clamp((x - y * 0.35f) / s + 0.1f, 0, 1); return Canvas.Mix(A, B, t); }
        float lw = Math.Max(1f, s * 0.06f);
        for (int i = 1; i < pts.Count; i++)
        {
            var (x0, y0, _) = pts[0]; var (x1, y1, _) = pts[i];
            float dx = x1 - x0, dy = y1 - y0, len2 = dx * dx + dy * dy;
            Put((px2, py2) =>
            {
                float t = Math.Clamp(((px2 - x0) * dx + (py2 - y0) * dy) / len2, 0, 1);
                float ex = px2 - (x0 + t * dx), ey = py2 - (y0 + t * dy);
                return MathF.Sqrt(ex * ex + ey * ey) - lw / 2;
            }, ColorAt, 0.55f);
        }
        foreach (var (x, y, r) in pts)
            Put((px2, py2) => MathF.Sqrt((px2 - x) * (px2 - x) + (py2 - y) * (py2 - y)) - r, ColorAt, 1);
        for (int n = 0; n < size * size; n++)
        {
            uint rgb = col[n];
            if (paused)
            {
                uint g = (((rgb >> 16) & 0xFF) * 30 + ((rgb >> 8) & 0xFF) * 59 + (rgb & 0xFF) * 11) / 100;
                g = (uint)(g * 0.55f + 0x80 * 0.45f);
                rgb = (g << 16) | (g << 8) | g;
            }
            px[n] = ((uint)(cov[n] * 255 + 0.5f) << 24) | rgb;
        }
        // CreateIconIndirect copies the bitmaps; the 32-bit colour bitmap's alpha is the shape
        var bi = new BITMAPINFOHEADER { biSize = sizeof(BITMAPINFOHEADER), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
        var color = CreateDIBSection(IntPtr.Zero, ref bi, 0, out var bits, IntPtr.Zero, 0);
        fixed (uint* src = px) Buffer.MemoryCopy(src, (void*)bits, size * size * 4, size * size * 4);
        var zeros = new byte[((size + 15) / 16) * 2 * size];            // 1-bpp rows are WORD aligned
        IntPtr mask;
        fixed (byte* z = zeros) mask = CreateBitmap(size, size, 1, 1, (IntPtr)z);
        var info = new ICONINFO { fIcon = 1, hbmColor = color, hbmMask = mask };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }
}
