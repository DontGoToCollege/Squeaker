using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Squeaker.Core;

/// <summary>A picture as straight-alpha BGRA bytes, the form the recolouring and the .cur writer both want.</summary>
public sealed class Pix(int w, int h, byte[]? p = null)
{
    public int W { get; } = w;
    public int H { get; } = h;
    public byte[] P { get; } = p ?? new byte[w * h * 4];

    public Pix Clone() => new(W, H, (byte[])P.Clone());

    public BitmapSource ToBitmap()
    {
        var b = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, P, W * 4);
        b.Freeze();
        return b;
    }

    public static Pix FromBitmap(BitmapSource src)
    {
        var c = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var pix = new Pix(c.PixelWidth, c.PixelHeight);
        c.CopyPixels(pix.P, pix.W * 4, 0);
        return pix;
    }

    /// <summary>Draws this picture into a w×h canvas at (x, y, dw, dh) with high-quality resampling.</summary>
    public Pix Render(int w, int h, double x, double y, double dw, double dh)
    {
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) dc.DrawImage(ToBitmap(), new Rect(x, y, dw, dh));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return FromBitmap(rtb);
    }
}

/// <summary>One cursor image and its hotspot, in that image's pixels.</summary>
public sealed record Frame(Pix Img, double HotX, double HotY);

/// <summary>A cursor as Squeaker works on it: one or more frames, plus the play order for animated ones.</summary>
public sealed class CursorArt
{
    public List<Frame> Frames { get; } = [];
    /// <summary>Play order as (frame index, duration in 1/60 s). Empty for a still cursor.</summary>
    public List<(int Frame, int Jiffies)> Steps { get; } = [];
    /// <summary>False when the source had no hotspot of its own (plain images, .ico files).</summary>
    public bool HasHotspot { get; set; } = true;
    public bool Animated => Frames.Count > 1;
}

/// <summary>Reads .cur, .ani, .ico and ordinary images; writes .cur and .ani.</summary>
public static class CurIO
{
    public const int MaxBytes = 20 * 1024 * 1024;

    public static string Sniff(byte[] d)
    {
        if (d.Length >= 12 && Ascii(d, 0, 4) == "RIFF" && Ascii(d, 8, 4) == "ACON") return "ani";
        if (d.Length >= 6 && d[0] == 0 && d[1] == 0 && d[3] == 0 && d[2] == 2) return "cur";
        if (d.Length >= 6 && d[0] == 0 && d[1] == 0 && d[3] == 0 && d[2] == 1) return "ico";
        if (d.Length >= 4 && d[0] == 'P' && d[1] == 'K' && d[2] == 3 && d[3] == 4) return "zip";
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return "png";
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "jpg";
        if (d.Length >= 6 && Ascii(d, 0, 3) == "GIF") return "gif";
        if (d.Length >= 2 && d[0] == 'B' && d[1] == 'M') return "bmp";
        if (d.Length >= 12 && Ascii(d, 0, 4) == "RIFF" && Ascii(d, 8, 4) == "WEBP") return "webp";
        return "";
    }

    public static bool IsCursorData(byte[] d) => Sniff(d) is "ani" or "cur" or "ico" or "png" or "jpg" or "gif" or "bmp" or "webp";

    /// <summary>Loads any supported cursor or picture. Throws InvalidDataException for anything else.</summary>
    public static CursorArt Load(byte[] d)
    {
        var kind = Sniff(d);
        try
        {
            return kind switch
            {
                "ani" => LoadAni(d),
                "cur" or "ico" => Still(ReadIconDir(d, 0, d.Length)),
                "gif" => LoadGif(d),
                "png" or "jpg" or "bmp" or "webp" => Still(new Frame(Decode(d), 0, 0), hasHotspot: false),
                _ => throw new InvalidDataException("This isn't a cursor or a picture Squeaker can read."),
            };
        }
        catch (Exception e) when (e is not InvalidDataException)
        {
            throw new InvalidDataException("This file looks damaged: " + e.Message, e);
        }
    }

    static CursorArt Still((Frame f, bool hot) r) => Still(r.f, r.hot);
    static CursorArt Still(Frame f, bool hasHotspot = true)
    {
        var a = new CursorArt { HasHotspot = hasHotspot };
        a.Frames.Add(f);
        return a;
    }

    static Pix Decode(byte[] d)
    {
        var dec = BitmapDecoder.Create(new MemoryStream(d), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return Pix.FromBitmap(dec.Frames[0]);
    }

    /// <summary>The largest image in an icon or cursor directory, decoded by Windows' own ICO reader.</summary>
    static (Frame, bool) ReadIconDir(byte[] d, int off, int len)
    {
        bool isCur = BitConverter.ToUInt16(d, off + 2) == 2;
        int n = BitConverter.ToUInt16(d, off + 4);
        if (n == 0 || 6 + 16 * n > len) throw new InvalidDataException("Empty cursor.");
        int best = -1, bestW = 0;
        for (int i = 0; i < n; i++)
        {
            int w = d[off + 6 + 16 * i]; if (w == 0) w = 256;
            if (w > bestW) { bestW = w; best = i; }
        }
        int e = off + 6 + 16 * best;
        int hx = BitConverter.ToUInt16(d, e + 4), hy = BitConverter.ToUInt16(d, e + 6);
        int size = BitConverter.ToInt32(d, e + 8), at = BitConverter.ToInt32(d, e + 12);
        if (size <= 0 || at < 0 || at + size > len) throw new InvalidDataException("Cursor image runs past the end of the file.");

        // Repack that one image as a one-entry .ico so WIC decodes every bit depth and PNG for us.
        bool png = d[off + at] == 0x89;
        int bpp = png ? 32 : BitConverter.ToUInt16(d, off + at + 14);
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)1);
        bw.Write(d[e]); bw.Write(d[e + 1]); bw.Write(d[e + 2]); bw.Write((byte)0);
        bw.Write((ushort)1); bw.Write((ushort)bpp); bw.Write(size); bw.Write(22);
        bw.Write(d, off + at, size);
        ms.Position = 0;
        var dec = new IconBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var pix = Pix.FromBitmap(dec.Frames[0]);
        double scale = pix.W / (double)(d[e] == 0 ? 256 : d[e]);
        return (new Frame(pix, isCur ? hx * scale : 0, isCur ? hy * scale : 0), isCur);
    }

    static CursorArt LoadAni(byte[] d)
    {
        var art = new CursorArt();
        int nSteps = 0, rate = 6;
        int[]? rates = null, seq = null;
        void Walk(int pos, int end)
        {
            while (pos + 8 <= end)
            {
                string id = Ascii(d, pos, 4);
                int len = BitConverter.ToInt32(d, pos + 4), body = pos + 8;
                if (len < 0 || body + len > end) break;
                switch (id)
                {
                    case "LIST": Walk(body + 4, body + len); break;
                    case "anih":
                        nSteps = BitConverter.ToInt32(d, body + 8);
                        rate = Math.Max(1, BitConverter.ToInt32(d, body + 28));
                        break;
                    case "rate": rates = Ints(d, body, len / 4); break;
                    case "seq ": seq = Ints(d, body, len / 4); break;
                    case "icon":
                        var (f, hot) = ReadIconDir(d, body, len);
                        art.Frames.Add(f);
                        if (!hot) art.HasHotspot = false;
                        break;
                }
                pos = body + len + (len & 1);
            }
        }
        Walk(12, d.Length);
        if (art.Frames.Count == 0) throw new InvalidDataException("This animated cursor has no frames.");
        if (nSteps <= 0) nSteps = seq?.Length ?? art.Frames.Count;
        for (int i = 0; i < nSteps; i++)
        {
            int f = seq != null && i < seq.Length ? seq[i] : i;
            if (f < 0 || f >= art.Frames.Count) f = i % art.Frames.Count;
            int j = rates != null && i < rates.Length && rates[i] > 0 ? rates[i] : rate;
            art.Steps.Add((f, j));
        }
        return art;
    }

    /// <summary>Animated GIFs become animated cursors; frames are composed the way browsers show them.</summary>
    static CursorArt LoadGif(byte[] d)
    {
        var dec = new GifBitmapDecoder(new MemoryStream(d), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var art = new CursorArt { HasHotspot = false };
        if (dec.Frames.Count == 1) { art.Frames.Add(new Frame(Pix.FromBitmap(dec.Frames[0]), 0, 0)); return art; }

        int W = Meta(dec.Metadata, "/logscrdesc/Width", dec.Frames[0].PixelWidth);
        int H = Meta(dec.Metadata, "/logscrdesc/Height", dec.Frames[0].PixelHeight);
        var canvas = new Pix(W, H);
        // shortcut: at most 64 frames, plenty for a pointer; longer GIFs are cut off.
        foreach (var fr in dec.Frames.Take(64))
        {
            var m = fr.Metadata as BitmapMetadata;
            int left = Meta(m, "/imgdesc/Left", 0), top = Meta(m, "/imgdesc/Top", 0);
            int delay = Meta(m, "/grctlext/Delay", 10), disposal = Meta(m, "/grctlext/Disposal", 0);
            var px = Pix.FromBitmap(fr);
            for (int y = 0; y < px.H; y++)
                for (int x = 0; x < px.W; x++)
                {
                    int cx = x + left, cy = y + top;
                    if (cx >= W || cy >= H) continue;
                    int s = (y * px.W + x) * 4;
                    if (px.P[s + 3] == 0) continue;
                    Array.Copy(px.P, s, canvas.P, (cy * W + cx) * 4, 4);
                }
            art.Frames.Add(new Frame(canvas.Clone(), 0, 0));
            art.Steps.Add((art.Frames.Count - 1, Math.Max(2, (delay <= 1 ? 10 : delay) * 60 / 100)));
            if (disposal == 2)
                for (int y = top; y < Math.Min(H, top + px.H); y++)
                    Array.Clear(canvas.P, (y * W + left) * 4, Math.Max(0, Math.Min(px.W, W - left)) * 4);
        }
        return art;
    }

    static int Meta(ImageMetadata? m, string q, int fallback)
    {
        try { return m is BitmapMetadata b && b.GetQuery(q) is { } v ? Convert.ToInt32(v) : fallback; }
        catch { return fallback; }
    }

    // ---------- writing ----------

    /// <summary>A .cur holding the same frame at several sizes, so Windows picks a sharp one on every DPI.</summary>
    public static byte[] WriteCur(IReadOnlyList<(Pix Img, int HotX, int HotY)> sizes)
    {
        var dibs = sizes.Select(s => Dib(s.Img)).ToList();
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write((ushort)0); bw.Write((ushort)2); bw.Write((ushort)sizes.Count);
        int offset = 6 + 16 * sizes.Count;
        for (int i = 0; i < sizes.Count; i++)
        {
            var (img, hx, hy) = sizes[i];
            bw.Write((byte)(img.W % 256)); bw.Write((byte)(img.H % 256)); bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((ushort)hx); bw.Write((ushort)hy); bw.Write(dibs[i].Length); bw.Write(offset);
            offset += dibs[i].Length;
        }
        foreach (var dib in dibs) bw.Write(dib);
        return ms.ToArray();
    }

    static byte[] Dib(Pix p)
    {
        int maskStride = (p.W + 31) / 32 * 4;
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(40); bw.Write(p.W); bw.Write(p.H * 2); bw.Write((ushort)1); bw.Write((ushort)32);
        bw.Write(0); bw.Write(p.W * p.H * 4 + maskStride * p.H); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        for (int y = p.H - 1; y >= 0; y--) bw.Write(p.P, y * p.W * 4, p.W * 4);
        var row = new byte[maskStride];
        for (int y = p.H - 1; y >= 0; y--)
        {
            Array.Clear(row);
            for (int x = 0; x < p.W; x++)
                if (p.P[(y * p.W + x) * 4 + 3] == 0) row[x >> 3] |= (byte)(0x80 >> (x & 7));
            bw.Write(row);
        }
        return ms.ToArray();
    }

    /// <summary>A .ani from ready-made .cur files and a play order.</summary>
    public static byte[] WriteAni(IReadOnlyList<byte[]> frames, IReadOnlyList<(int Frame, int Jiffies)> steps)
    {
        var body = new MemoryStream();
        var bw = new BinaryWriter(body);
        bw.Write(Encoding.ASCII.GetBytes("ACON"));
        Chunk(bw, "anih", w =>
        {
            w.Write(36); w.Write(frames.Count); w.Write(steps.Count);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            w.Write(steps.Count > 0 ? steps[0].Jiffies : 6);
            w.Write(3);   // AF_ICON | AF_SEQUENCE
        });
        Chunk(bw, "rate", w => { foreach (var s in steps) w.Write(s.Jiffies); });
        Chunk(bw, "seq ", w => { foreach (var s in steps) w.Write(s.Frame); });
        Chunk(bw, "LIST", w =>
        {
            w.Write(Encoding.ASCII.GetBytes("fram"));
            foreach (var f in frames) Chunk(w, "icon", x => x.Write(f));
        });
        var ms = new MemoryStream();
        var outw = new BinaryWriter(ms);
        outw.Write(Encoding.ASCII.GetBytes("RIFF")); outw.Write((int)body.Length); outw.Write(body.ToArray());
        return ms.ToArray();
    }

    static void Chunk(BinaryWriter bw, string id, Action<BinaryWriter> fill)
    {
        var ms = new MemoryStream();
        fill(new BinaryWriter(ms));
        bw.Write(Encoding.ASCII.GetBytes(id)); bw.Write((int)ms.Length); bw.Write(ms.ToArray());
        if ((ms.Length & 1) == 1) bw.Write((byte)0);
    }

    static string Ascii(byte[] d, int at, int n) => Encoding.ASCII.GetString(d, at, n);
    static int[] Ints(byte[] d, int at, int n) => Enumerable.Range(0, n).Select(i => BitConverter.ToInt32(d, at + i * 4)).ToArray();
}
