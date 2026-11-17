using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Squeaker.Core;

/// <summary>One of Windows' pointer roles. Order matters: it is the order of a scheme string and of install.inf files.</summary>
public sealed record CursorRole(string Reg, string Name, string? Aero, double DefHotX, double DefHotY);

/// <summary>A ready-made look: fill (top to bottom), outline and accent.</summary>
public sealed record CursorStyle(string Name, string FillTop, string FillBottom, string Outline, string? Accent);

public sealed class Built
{
    public required byte[] File { get; init; }
    public required bool Animated { get; init; }
    public required BitmapSource Preview { get; init; }
    /// <summary>Hotspot as a fraction of the preview.</summary>
    public double HotX { get; init; }
    public double HotY { get; init; }
    public string Ext => Animated ? ".ani" : ".cur";
}

/// <summary>Turns a style, a size and the user's custom pictures into real cursor files.</summary>
public static class Forge
{
    public static readonly CursorRole[] Roles =
    [
        new("Arrow", "Normal select", "aero_arrow.cur", 0, 0),
        new("Help", "Help select", "aero_helpsel.cur", 0, 0),
        new("AppStarting", "Working in background", "aero_working.ani", 0, 0),
        new("Wait", "Busy", "aero_busy.ani", .5, .5),
        new("Crosshair", "Precision select", null, .5, .5),
        new("IBeam", "Text select", null, .5, .5),
        new("NWPen", "Handwriting", "aero_pen.cur", 0, 1),
        new("No", "Unavailable", "aero_unavail.cur", .5, .5),
        new("SizeNS", "Vertical resize", "aero_ns.cur", .5, .5),
        new("SizeWE", "Horizontal resize", "aero_ew.cur", .5, .5),
        new("SizeNWSE", "Diagonal resize 1", "aero_nwse.cur", .5, .5),
        new("SizeNESW", "Diagonal resize 2", "aero_nesw.cur", .5, .5),
        new("SizeAll", "Move", "aero_move.cur", .5, .5),
        new("UpArrow", "Alternate select", "aero_up.cur", .5, 0),
        new("Hand", "Link select", "aero_link.cur", .3, 0),
        new("Pin", "Location select", "aero_pin.cur", 0, 0),
        new("Person", "Person select", "aero_person.cur", 0, 0),
    ];

    public static readonly CursorStyle[] Styles =
    [
        new("Classic", "#FFFFFF", "#FFFFFF", "#000000", null),
        new("Midnight", "#1C1C1E", "#1C1C1E", "#F5F5F7", "#0A84FF"),
        new("Graphite", "#8E8E93", "#48484A", "#FFFFFF", "#AEAEB2"),
        new("Ocean", "#64D2FF", "#0A60FF", "#0B1F3A", "#64D2FF"),
        new("Sunset", "#FFD60A", "#FF375F", "#2B0A12", "#FF9F0A"),
        new("Mint", "#A6F4C5", "#30D158", "#06281A", "#30D158"),
        new("Lavender", "#E4D4FF", "#BF5AF2", "#22093A", "#BF5AF2"),
        new("Rose", "#FFC2D1", "#FF2D55", "#3A0010", "#FF2D55"),
    ];

    public static CursorRole Role(string reg) => Roles.First(r => r.Reg == reg);
    static string CursorsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors");

    /// <summary>The sizes written into each file: the chosen one first, then larger ones for high-DPI screens.</summary>
    public static int[] Sizes(int size) =>
        new[] { 1.0, 1.25, 1.5, 2.0 }.Select(k => Math.Min(256, (int)Math.Round(size * k / 2) * 2)).Distinct().ToArray();

    /// <summary>The cursor for one role with these settings, or null when Windows has no picture for it to start from.</summary>
    public static Built? Build(CursorRole role, Settings s, bool previewOnly = false, int[]? only = null)
    {
        var (art, tint) = Source(role, s);
        if (art == null) return null;
        s.Custom.TryGetValue(role.Reg, out var custom);

        var frames = art.Frames.Select(f =>
        {
            var hot = (x: f.HotX, y: f.HotY);
            if (custom?.HotX is double cx && custom.HotY is double cy) hot = (cx * f.Img.W, cy * f.Img.H);
            else if (!art.HasHotspot) hot = (role.DefHotX * (f.Img.W - 1), role.DefHotY * (f.Img.H - 1));
            var img = tint ? Recolor(f.Img, s) : f.Img;
            return new Frame(img, hot.x, hot.y);
        }).ToList();

        var sizes = only ?? (previewOnly ? [s.Size] : Sizes(s.Size));
        var first = Fit(frames[0], s.Size);
        var preview = first.Img.ToBitmap();
        double hx = first.HotX / (double)s.Size, hy = first.HotY / (double)s.Size;
        if (previewOnly) return new Built { File = [], Animated = art.Animated, Preview = preview, HotX = hx, HotY = hy };

        byte[] Cur(Frame f) => CurIO.WriteCur(sizes.Select(n => Fit(f, n)).ToList());
        var file = art.Animated
            ? CurIO.WriteAni(frames.Select(Cur).ToList(), art.Steps.Count > 0 ? art.Steps : frames.Select((_, i) => (i, 6)).ToList())
            : Cur(frames[0]);
        return new Built { File = file, Animated = art.Animated, Preview = preview, HotX = hx, HotY = hy };
    }

    static (CursorArt?, bool tint) Source(CursorRole role, Settings s)
    {
        if (s.Custom.TryGetValue(role.Reg, out var c) && File.Exists(c.File))
        {
            try { return (CurIO.Load(File.ReadAllBytes(c.File)), c.Tint); }
            catch (InvalidDataException) { /* a broken custom file falls back to the style */ }
        }
        if (role.Aero == null) return (Drawn(role.Reg), true);
        var path = Path.Combine(CursorsDir, role.Aero);
        return File.Exists(path) ? (CurIO.Load(File.ReadAllBytes(path)), true) : (null, false);
    }

    /// <summary>A point clicked on the squared preview, as a fraction of the user's original picture.</summary>
    public static (double X, double Y) PreviewToSource(CustomCursor c, double fx, double fy)
    {
        var img = CurIO.Load(File.ReadAllBytes(c.File)).Frames[0].Img;
        int side = Math.Max(img.W, img.H);
        return (Math.Clamp((fx * side - (side - img.W) / 2.0) / img.W, 0, 1),
                Math.Clamp((fy * side - (side - img.H) / 2.0) / img.H, 0, 1));
    }

    /// <summary>Squares the frame (centred) and scales it to n×n, moving the hotspot with it.</summary>
    static (Pix Img, int HotX, int HotY) Fit(Frame f, int n)
    {
        int side = Math.Max(f.Img.W, f.Img.H);
        double k = n / (double)side, ox = (side - f.Img.W) / 2.0, oy = (side - f.Img.H) / 2.0;
        var img = f.Img.W == n && f.Img.H == n ? f.Img : f.Img.Render(n, n, ox * k, oy * k, f.Img.W * k, f.Img.H * k);
        return (img, Math.Clamp((int)Math.Round((f.HotX + ox) * k), 0, n - 1), Math.Clamp((int)Math.Round((f.HotY + oy) * k), 0, n - 1));
    }

    /// <summary>Text and precision cursors are built into Windows rather than files, so Squeaker draws its own.</summary>
    static CursorArt Drawn(string reg)
    {
        var geo = Geometry.Parse(reg == "IBeam"
            ? "M12.5,7 Q16,7 16,10 Q16,7 19.5,7 M16,10 V22 M12.5,25 Q16,25 16,22 Q16,25 19.5,25"
            : "M16,4.5 V12.5 M16,19.5 V27.5 M4.5,16 H12.5 M19.5,16 H27.5");
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(4, 4));
            dc.DrawGeometry(null, new Pen(Brushes.Black, 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geo);
            dc.DrawGeometry(null, new Pen(Brushes.White, 1.3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geo);
        }
        var rtb = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var art = new CursorArt();
        art.Frames.Add(new Frame(Pix.FromBitmap(rtb), 64, 64));
        return art;
    }

    /// <summary>
    /// Windows' cursors are white fills inside black outlines. Brightness decides how far each pixel sits between
    /// outline and fill, so anti-aliasing survives; coloured pixels take the accent's hue; the soft shadow stays.
    /// </summary>
    public static Pix Recolor(Pix src, Settings s)
    {
        var top = Parse(s.FillTop); var bottom = Parse(s.FillBottom); var line = Parse(s.Outline);
        Color? accent = s.Accent is null ? null : Parse(s.Accent);
        var p = src.Clone();
        int minY = p.H, maxY = 0;
        for (int i = 3; i < p.P.Length; i += 4)
            if (p.P[i] > 150) { int y = i / 4 / p.W; minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        double span = Math.Max(1, maxY - minY);

        for (int i = 0; i < p.P.Length; i += 4)
        {
            byte a = p.P[i + 3];
            if (a == 0) continue;
            double b = p.P[i] / 255.0, g = p.P[i + 1] / 255.0, r = p.P[i + 2] / 255.0;
            double l = 0.299 * r + 0.587 * g + 0.114 * b;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            if (l < 0.35 && a < 150) continue;                // the drop shadow
            Color c;
            if (max - min > 0.2)
            {
                if (accent is not Color ac) continue;
                c = WithLightness(ac, (max + min) / 2);
            }
            else
            {
                double t = Math.Clamp(((i / 4 / p.W) - minY) / span, 0, 1);
                c = Lerp(line, Lerp(top, bottom, t), l);
            }
            p.P[i] = c.B; p.P[i + 1] = c.G; p.P[i + 2] = c.R;
        }
        return p;
    }

    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>The accent's hue and saturation at the given HSL lightness.</summary>
    static Color WithLightness(Color c, double l)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double al = (max + min) / 2;
        // Shift every channel by the same amount, then squeeze toward grey only if it would clip.
        double shift = l - al;
        double[] ch = [r + shift, g + shift, b + shift];
        double hi = ch.Max(), lo = ch.Min();
        double k = hi > 1 ? (1 - l) / (hi - l) : lo < 0 ? l / (l - lo) : 1;
        if (d == 0) k = 1;
        return Color.FromRgb((byte)(255 * Math.Clamp(l + (ch[0] - l) * k, 0, 1)),
                             (byte)(255 * Math.Clamp(l + (ch[1] - l) * k, 0, 1)),
                             (byte)(255 * Math.Clamp(l + (ch[2] - l) * k, 0, 1)));
    }
}
