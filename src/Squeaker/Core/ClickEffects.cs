using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using D = System.Drawing;
using D2 = System.Drawing.Drawing2D;

namespace Squeaker.Core;

/// <summary>
/// Watches clicks anywhere on screen and plays a short animation under the pointer. Each animation is its own
/// tiny click-through layered window, so nothing ever takes focus or blocks the click.
/// </summary>
public sealed class ClickEffects : IDisposable
{
    public static readonly string[] Kinds = ["Ripple", "Pulse", "Burst", "Halo", "Focus"];

    EffectSettings cfg = new();
    IntPtr hook;
    Thread? watcher;
    uint watcherId;
    readonly HookProc proc;
    readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    readonly List<Effect> live = [];
    readonly DispatcherTimer timer;
    SoundPlayer? squeak;

    public ClickEffects()
    {
        proc = OnMouse;   // kept in a field so the GC never collects the callback Windows holds
        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) => Tick();
    }

    /// <summary>True while the click watcher is installed.</summary>
    public bool Watching => hook != IntPtr.Zero;

    public void Update(EffectSettings s)
    {
        cfg = s;
        if (s.Enabled && watcher == null) StartWatcher();
        else if (!s.Enabled && watcher != null) StopWatcher();
    }

    /// <summary>
    /// The mouse hook lives on its own thread. Windows silently removes a low-level hook that answers slowly,
    /// which happened when it shared the UI thread with something busy, such as building cursors during Apply.
    /// </summary>
    void StartWatcher()
    {
        using var ready = new ManualResetEventSlim();
        watcher = new Thread(() =>
        {
            watcherId = GetCurrentThreadId();
            hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
            ready.Set();
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "Squeaker click watcher" };
        watcher.Start();
        ready.Wait(2000);
    }

    void StopWatcher()
    {
        PostThreadMessage(watcherId, 0x12, IntPtr.Zero, IntPtr.Zero);   // WM_QUIT ends its message loop
        watcher?.Join(2000);
        watcher = null;
    }

    IntPtr OnMouse(int code, IntPtr msg, IntPtr data)
    {
        if (code >= 0)
        {
            int m = (int)msg;
            bool right = m == WM_RBUTTONDOWN;
            if (m == WM_LBUTTONDOWN || m == WM_MBUTTONDOWN || (right && cfg.RightClicks))
            {
                var pt = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(data);
                // Leave the hook straight away; drawing happens on the next turn of the message loop.
                ui.BeginInvoke(() => Play(pt.x, pt.y, right));
            }
        }
        return CallNextHookEx(hook, code, msg, data);
    }

    /// <summary>Plays the effect at a screen point in physical pixels. Also used by the in-app preview.</summary>
    public void Play(int x, int y, bool right, EffectSettings? s = null)
    {
        s ??= cfg;
        GetDpiForMonitor(MonitorFromPoint(new POINT { x = x, y = y }, 2), 0, out uint dpi, out _);
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;
        var color = D.ColorTranslator.FromHtml(right ? s.RightColor : s.LeftColor);
        live.Add(new Effect(x, y, s.Kind, color, s.Size * scale, scale, Math.Max(120, s.Duration)));
        if (s.Squeak) Squeak();
        timer.Start();
        Tick();
    }

    void Tick()
    {
        for (int i = live.Count - 1; i >= 0; i--)
            if (!live[i].Draw()) { live[i].Dispose(); live.RemoveAt(i); }
        if (live.Count == 0) timer.Stop();
    }

    void Squeak()
    {
        squeak ??= new SoundPlayer(new MemoryStream(SqueakWav()));
        squeak.Play();
    }

    /// <summary>A tiny rising-and-falling chirp, made on the spot so no sound file is shipped.</summary>
    public static byte[] SqueakWav()
    {
        const int rate = 44100;
        int n = (int)(rate * 0.12);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(n * 2);
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)n;
            double f = 2100 + 1700 * Math.Sin(Math.PI * t) + 90 * Math.Sin(2 * Math.PI * 38 * t * 0.12);
            phase += 2 * Math.PI * f / rate;
            double env = Math.Pow(Math.Sin(Math.PI * t), 0.6);
            w.Write((short)(Math.Sin(phase) * env * 0.22 * short.MaxValue));
        }
        return ms.ToArray();
    }

    /// <summary>A still of an effect partway through, for the picker tiles.</summary>
    public static System.Windows.Media.Imaging.BitmapSource Still(string kind, string color, int side, double t)
    {
        using var bmp = new D.Bitmap(side, side, D.Imaging.PixelFormat.Format32bppArgb);
        using (var g = D.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = D2.SmoothingMode.AntiAlias;
            Effect.Paint(g, kind, D.ColorTranslator.FromHtml(color), side * 0.42, side / 56.0, t, side / 2f);
        }
        var data = bmp.LockBits(new D.Rectangle(0, 0, side, side), D.Imaging.ImageLockMode.ReadOnly, D.Imaging.PixelFormat.Format32bppArgb);
        var src = System.Windows.Media.Imaging.BitmapSource.Create(side, side, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, data.Scan0, data.Stride * side, data.Stride);
        bmp.UnlockBits(data);
        src.Freeze();
        return src;
    }

    public void Dispose()
    {
        if (watcher != null) StopWatcher();
        foreach (var e in live) e.Dispose();
        live.Clear();
    }

    sealed class Effect : System.Windows.Forms.NativeWindow, IDisposable
    {
        readonly int cx, cy, side;
        readonly string kind;
        readonly D.Color color;
        readonly double radius, scale, duration;
        readonly Stopwatch clock = Stopwatch.StartNew();
        bool shown;

        public Effect(int x, int y, string kind, D.Color color, double size, double scale, double duration)
        {
            (cx, cy, this.kind, this.color, radius, this.scale, this.duration) = (x, y, kind, color, size / 2, scale, duration);
            side = (int)Math.Ceiling(radius * 2 + 8 * scale);
            CreateHandle(new System.Windows.Forms.CreateParams
            {
                Style = unchecked((int)0x80000000),                                // WS_POPUP
                ExStyle = 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8,                  // layered, click-through, tool, no-activate, topmost
                X = x - side / 2, Y = y - side / 2, Width = side, Height = side,
            });
        }

        /// <summary>Draws the current moment; false once the animation is over.</summary>
        public bool Draw()
        {
            double t = clock.Elapsed.TotalMilliseconds / duration;
            if (t >= 1 || Handle == IntPtr.Zero) return false;
            using var bmp = new D.Bitmap(side, side, D.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = D.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = D2.SmoothingMode.AntiAlias;
                g.Clear(D.Color.Transparent);
                Paint(g, kind, color, radius, scale, t, side / 2f);
            }
            Present(bmp);
            return true;
        }

        public static void Paint(D.Graphics g, string kind, D.Color color, double R, double scale, double t, float c)
        {
            double e = 1 - Math.Pow(1 - t, 3), fade = 1 - t;
            D.Color A(double a) => D.Color.FromArgb((int)Math.Clamp(a * 255, 0, 255), color);
            void Ring(double r, double width, double a)
            {
                using var pen = new D.Pen(A(a), (float)Math.Max(1, width));
                g.DrawEllipse(pen, (float)(c - r), (float)(c - r), (float)(2 * r), (float)(2 * r));
            }
            void Disc(double x, double y, double r, double a)
            {
                using var b = new D.SolidBrush(A(a));
                g.FillEllipse(b, (float)(x - r), (float)(y - r), (float)(2 * r), (float)(2 * r));
            }
            switch (kind)
            {
                case "Pulse":
                    Disc(c, c, R * e, 0.4 * fade);
                    Disc(c, c, R * 0.2 * fade, 0.9 * fade);
                    break;
                case "Burst":
                    for (int i = 0; i < 10; i++)
                    {
                        double ang = i * Math.PI / 5, d = R * (0.15 + 0.8 * e);
                        Disc(c + Math.Cos(ang) * d, c + Math.Sin(ang) * d, R * 0.1 * fade + 0.6 * scale, fade);
                    }
                    Ring(R * 0.45 * e, 1.5 * scale, 0.5 * fade);
                    break;
                case "Halo":
                    Ring(R * e, 2 * scale, fade);
                    double t2 = Math.Clamp((t - 0.22) / 0.78, 0, 1);
                    if (t2 > 0) Ring(R * (1 - Math.Pow(1 - t2, 3)) * 0.92, 2 * scale, (1 - t2) * 0.8);
                    break;
                case "Focus":
                    Ring(R * (1 - 0.7 * e), 2.5 * scale, Math.Pow(fade, 0.6));
                    Disc(c, c, 2.5 * scale, Math.Min(1, t * 4) * fade);
                    break;
                default:   // Ripple
                    Disc(c, c, R * (0.25 + 0.75 * e), 0.14 * fade);
                    Ring(R * (0.25 + 0.75 * e), R * 0.1 * fade + 1.2 * scale, Math.Pow(fade, 1.2) * 0.95);
                    break;
            }
        }

        void Present(D.Bitmap bmp)
        {
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
            IntPtr hbmp = bmp.GetHbitmap(D.Color.FromArgb(0)), old = SelectObject(mem, hbmp);
            var size = new SIZE { cx = side, cy = side };
            var src = new POINT();
            var dst = new POINT { x = cx - side / 2, y = cy - side / 2 };
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2);
            SelectObject(mem, old); DeleteObject(hbmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
            if (!shown) { ShowWindow(Handle, 4); shown = true; }   // SW_SHOWNOACTIVATE
        }

        public void Dispose() { if (Handle != IntPtr.Zero) DestroyHandle(); }
    }

    // ---------- Win32 ----------
    const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207;
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public int x, y, mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public POINT pt; }
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT pDst, ref SIZE size, IntPtr src, ref POINT pSrc, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
}
