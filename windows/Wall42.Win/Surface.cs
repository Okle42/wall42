using System.Numerics;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Wall42.Native;

namespace Wall42;

/// One per monitor: a child window under the desktop WorkerW + a flip-model swap chain. It owns no
/// particles; it only draws its part of the shared World. Each monitor pauses on its own (occlusion).
sealed class Surface : IDisposable
{
    public readonly Monitor Mon;
    public readonly ScreenSlot Slot;
    public IntPtr Hwnd { get; private set; }
    readonly Gpu gpu;
    readonly float unit;
    IDXGISwapChain1? swap;
    ID3D11RenderTargetView? rtv;
    readonly int w, h;
    public bool Dead;                    // window destroyed from outside (Explorer restart) or GPU lost
    bool closing;

    // pause state
    public bool Occluded;
    public double CoveredSince = -1;
    public string CoveredBy = "";
    public bool Drawing;
    public double PausedAt;
    public bool Trimmed;
    // pacing
    public bool Focused = true;
    public double Interval = 1 / 30.0;
    public double NextDue;
    public int Frames, LastFrames;

    public const string ClassName = "wall42.surface";

    public Surface(Gpu gpu, Monitor mon, ScreenSlot slot, float unit, IntPtr worker)
    {
        this.gpu = gpu; Mon = mon; Slot = slot; this.unit = unit;
        w = mon.Rect.Width; h = mon.Rect.Height;
        // position inside the WorkerW's client area (it spans the whole virtual screen)
        var p = new POINT(mon.Rect.Left, mon.Rect.Top);
        ScreenToClient(worker, ref p);
        // never activates, never takes clicks (disabled + transparent + HTTRANSPARENT); the icons sit above anyway
        const int ex = WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW;
        Hwnd = CreateWindowExW(ex, ClassName, "wall42", WS_CHILD | WS_DISABLED | WS_CLIPSIBLINGS,
            p.X, p.Y, w, h, worker, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (Hwnd == IntPtr.Zero)
        {
            // some shells refuse a cross-process parent at creation: create as a popup, then re-parent
            Hwnd = CreateWindowExW(ex, ClassName, "wall42", WS_POPUP | WS_DISABLED, mon.Rect.Left, mon.Rect.Top, w, h,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (Hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            SetWindowLongPtr(Hwnd, GWL_STYLE, (IntPtr)(long)(WS_CHILD | WS_DISABLED | WS_CLIPSIBLINGS));
            if (SetParent(Hwnd, worker) == IntPtr.Zero && GetParent(Hwnd) != worker)
                throw new InvalidOperationException($"SetParent failed {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }
        SetWindowPos(Hwnd, HWND_TOP, p.X, p.Y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        CreateSwapChain();
    }

    void CreateSwapChain()
    {
        var desc = new SwapChainDescription1
        {
            Width = (uint)w, Height = (uint)h, Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2, BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            // flip model: DWM takes our buffer as is, no extra copy (cheaper than the old blt model)
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
        };
        swap = gpu.Factory.CreateSwapChainForHwnd(gpu.Device, Hwnd, desc);
        gpu.Factory.MakeWindowAssociation(Hwnd, WindowAssociationFlags.IgnoreAll);     // no Alt+Enter full screen
        MakeTarget();
    }

    void MakeTarget()
    {
        using var back = swap!.GetBuffer<ID3D11Texture2D>(0);
        rtv = gpu.Device.CreateRenderTargetView(back);
    }

    /// Draw one frame of this monitor's view. false = present failed (device lost / window gone).
    public bool Render(World world, bool normalBlend, bool noDraw)
    {
        if (Dead || swap == null) return false;
        if (Trimmed) Untrim();
        var u = world.UniformsFor(new Vector2(w, h), Slot.Origin, Slot.Size, unit);
        gpu.Draw(rtv!, w, h, u, normalBlend, noDraw);
        var hr = swap.Present(1, PresentFlags.None);
        Frames++;
        if (hr.Failure) { Log.Note($"Present failed {hr} on {Mon.Device}"); Dead = true; return false; }
        return true;
    }

    /// Paused for a while: give the full-size buffers back (the Mac's releaseDrawables, memory −42% there).
    /// A tiny dark frame is presented so DWM drops its reference to the big one.
    public void Trim(Vector4 color)
    {
        if (Trimmed || swap == null || Dead) return;
        rtv?.Dispose(); rtv = null;
        gpu.Ctx.OMSetRenderTargets((ID3D11RenderTargetView?)null!);
        gpu.Ctx.Flush();
        if (swap.ResizeBuffers(2, 8, 8, Format.B8G8R8A8_UNorm, SwapChainFlags.None).Failure) { MakeTarget(); return; }
        MakeTarget();
        gpu.Ctx.ClearRenderTargetView(rtv!, new Vortice.Mathematics.Color4(color.X, color.Y, color.Z, 1));
        swap.Present(0, PresentFlags.None);
        Trimmed = true;
    }

    void Untrim()
    {
        rtv?.Dispose(); rtv = null;
        gpu.Ctx.OMSetRenderTargets((ID3D11RenderTargetView?)null!);
        gpu.Ctx.Flush();
        swap!.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        MakeTarget();
        Trimmed = false;
    }

    /// The surface's own WM_DESTROY: if we didn't ask for it, Explorer took the WorkerW (and us) down.
    public void OnDestroyed() { if (!closing) Dead = true; Hwnd = IntPtr.Zero; }

    public void Dispose()
    {
        closing = true;
        rtv?.Dispose(); rtv = null;
        swap?.Dispose(); swap = null;
        if (Hwnd != IntPtr.Zero && IsWindow(Hwnd)) DestroyWindow(Hwnd);
        Hwnd = IntPtr.Zero;
    }
}
