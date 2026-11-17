using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Squeaker.Core;

namespace Squeaker;

/// <summary>
/// `Squeaker.exe --selftest` checks the parts that must never be wrong, without touching the real cursors:
/// files are written to a scratch folder and registry work goes to a test key. Exit code 0 means all passed.
/// </summary>
public static class SelfTest
{
    static readonly string Log = Path.Combine(Path.GetTempPath(), "squeaker-selftest.log");

    public static int Run()
    {
        var fails = new List<string>();
        void Check(bool ok, string what) { if (!ok) fails.Add(what); }
        var scratch = Path.Combine(Path.GetTempPath(), "squeaker-selftest-" + Environment.ProcessId);
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("SQUEAKER_HOME", scratch);
        const string testKey = @"Software\Squeaker\SelfTest\Cursors";
        try
        {
            // .cur and .ani round trips
            var pix = new Pix(40, 30);
            for (int i = 0; i < pix.P.Length; i += 4) { pix.P[i] = 200; pix.P[i + 3] = (byte)(i % 3 == 0 ? 0 : 255); }
            var cur = CurIO.Load(CurIO.WriteCur([(pix, 3, 4)]));
            Check(cur.Frames[0].Img.W == 40 && cur.Frames[0].Img.H == 30, ".cur keeps its size");
            Check(cur.Frames[0].HotX == 3 && cur.Frames[0].HotY == 4, ".cur keeps its hotspot");
            Check(cur.Frames[0].Img.P[4 * 4 + 3] == 255 && cur.Frames[0].Img.P[3] == 0, ".cur keeps transparency");
            var frameFile = CurIO.WriteCur([(pix, 1, 1)]);
            var ani = CurIO.Load(CurIO.WriteAni([frameFile, frameFile, frameFile], [(0, 5), (2, 7), (1, 9)]));
            Check(ani.Frames.Count == 3 && ani.Steps.Count == 3, ".ani keeps frames and steps");
            Check(ani.Steps[1] == (2, 7), ".ani keeps play order and timing");

            // Windows' own cursors can be read
            var arrow = CurIO.Load(File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Cursors\aero_arrow.cur")));
            Check(arrow.Frames[0].Img.W == 128, "the largest frame of aero_arrow.cur is used");

            // Every role builds, at the chosen size first, and Windows itself accepts every file
            foreach (var style in Forge.Styles.Where(s => s.Name is "Classic" or "Sunset"))
            {
                var s = new Settings { FillTop = style.FillTop, FillBottom = style.FillBottom, Outline = style.Outline, Accent = style.Accent, Size = 48 };
                foreach (var role in Forge.Roles)
                {
                    var b = Forge.Build(role, s);
                    if (b == null) { fails.Add($"{role.Reg} builds"); continue; }
                    var first = b.Animated ? FirstIcon(b.File) : b.File;
                    Check(first[6] == 48, $"{role.Reg} ({style.Name}) starts with the 48 px image");
                    var path = Path.Combine(scratch, role.Reg + b.Ext);
                    File.WriteAllBytes(path, b.File);
                    var h = LoadImage(IntPtr.Zero, path, 2, 48, 48, 0x10);
                    Check(h != IntPtr.Zero, $"Windows loads {role.Reg}{b.Ext} ({style.Name})");
                    if (h != IntPtr.Zero) DestroyCursor(h);
                }
            }

            // Classic leaves Windows' arrow as it was; Sunset paints it warm
            var classic = Forge.Recolor(arrow.Frames[0].Img, new Settings());
            int diff = 0;
            for (int i = 0; i < classic.P.Length; i++) diff = Math.Max(diff, Math.Abs(classic.P[i] - arrow.Frames[0].Img.P[i]));
            Check(diff <= 3, "the Classic style keeps Windows' arrow unchanged");
            var sunsetStyle = Forge.Styles.First(s => s.Name == "Sunset");
            var sunset = Forge.Recolor(arrow.Frames[0].Img, new Settings { FillTop = sunsetStyle.FillTop, FillBottom = sunsetStyle.FillBottom, Outline = sunsetStyle.Outline });
            Check(Enumerable.Range(0, sunset.P.Length / 4).Any(i => sunset.P[i * 4 + 3] == 255 && sunset.P[i * 4 + 2] > 240 && sunset.P[i * 4] < 80), "Sunset gives the arrow a warm fill");

            // Size list starts with the chosen size and never goes past 256
            Check(Forge.Sizes(48).SequenceEqual([48, 60, 72, 96]), "sizes for 48 px");
            Check(Forge.Sizes(256).SequenceEqual([256]), "sizes cap at 256 px");

            // Cursor packs: install.inf order wins, file names are the fallback
            var inf = "[Scheme.Reg]\r\nHKCU,\"Control Panel\\Cursors\\Schemes\",\"%SCHEME_NAME%\",,\"%10%\\%CUR_DIR%\\%pointer%,%10%\\%CUR_DIR%\\%help%,%10%\\%CUR_DIR%\\%work%,%10%\\%CUR_DIR%\\%busy%,%10%\\%CUR_DIR%\\%cross%,%10%\\%CUR_DIR%\\%text%,%10%\\%CUR_DIR%\\%hand%,%10%\\%CUR_DIR%\\%unavailiable%,%10%\\%CUR_DIR%\\%vert%,%10%\\%CUR_DIR%\\%horz%,%10%\\%CUR_DIR%\\%dgn1%,%10%\\%CUR_DIR%\\%dgn2%,%10%\\%CUR_DIR%\\%move%,%10%\\%CUR_DIR%\\%alternate%,%10%\\%CUR_DIR%\\%link%\"\r\n" +
                      "[Strings]\r\nCUR_DIR = \"Cursors\\Test\"\r\nSCHEME_NAME = \"Test\"\r\npointer = \"Point A.cur\"\r\nhelp = \"h.cur\"\r\nwork = \"w.ani\"\r\nbusy = \"b.ani\"\r\ncross = \"c.cur\"\r\ntext = \"t.cur\"\r\nhand = \"pen.cur\"\r\nunavailiable = \"u.cur\"\r\nvert = \"v.cur\"\r\nhorz = \"z.cur\"\r\ndgn1 = \"d1.cur\"\r\ndgn2 = \"d2.cur\"\r\nmove = \"m.cur\"\r\nalternate = \"al.cur\"\r\nlink = \"Link X.cur\"\r\n";
            var scheme = Importer.SchemeFromInf(inf);
            Check(scheme is { Length: 15 } && scheme[0] == "Point A.cur" && scheme[14] == "Link X.cur", "install.inf scheme is read in order");
            Check(Importer.GuessRole("Normal Select.ani") == "Arrow", "guess: Normal Select");
            Check(Importer.GuessRole("Handwriting.cur") == "NWPen", "guess: Handwriting");
            Check(Importer.GuessRole("Diagonal Resize 2.cur") == "SizeNESW", "guess: Diagonal Resize 2");
            Check(Importer.GuessRole("up_arrow.cur") == "UpArrow", "guess: up_arrow");
            Check(Importer.GuessRole("link.cur") == "Hand", "guess: link");
            Check(Importer.GuessRole("notes.txt") == null, "guess: unrelated file");

            var zipBytes = new MemoryStream();
            using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, true))
            {
                void Add(string name, byte[] data) { using var s = zip.CreateEntry(name).Open(); s.Write(data); }
                Add("pack/install.inf", Encoding.ASCII.GetBytes(inf));
                Add("pack/Point A.cur", frameFile);
                Add("pack/Link X.cur", frameFile);
                Add("pack/Busy.ani", CurIO.WriteAni([frameFile, frameFile], [(0, 5), (1, 5)]));
                Add("../evil.cur", frameFile);
            }
            var pack = Importer.ImportPack(zipBytes.ToArray());
            Check(pack.ContainsKey("Arrow") && pack.ContainsKey("Hand") && pack.ContainsKey("Wait"), "a zip pack fills the right roles");
            Check(pack.Values.All(c => Path.GetDirectoryName(c.File) == Settings.CustomFolder), "pack files land only in Squeaker's own folder");
            Check(!File.Exists(Path.Combine(Path.GetDirectoryName(scratch)!, "evil.cur")), "zip paths are never followed");

            // A custom picture: hotspot from the user, size from the settings
            var png = new MemoryStream();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(new Pix(20, 10, Enumerable.Repeat((byte)255, 800).ToArray()).ToBitmap()));
            enc.Save(png);
            var custom = Importer.SaveForRole("Arrow", png.ToArray());
            custom.HotX = 1; custom.HotY = 1;
            var withCustom = new Settings { Size = 40, Custom = { ["Arrow"] = custom } };
            var built = Forge.Build(Forge.Role("Arrow"), withCustom)!;
            var back = CurIO.Load(built.File).Frames[0];
            Check(back.Img.W == 80 && Math.Abs(back.HotX - 79) <= 1 && Math.Abs(back.HotY - 59) <= 1, "a wide picture is centred and its hotspot follows");
            var (fx, fy) = Forge.PreviewToSource(custom, 0.5, 0.5);
            Check(Math.Abs(fx - 0.5) < 0.01 && Math.Abs(fy - 0.5) < 0.01, "preview clicks map back to the picture");

            // Applying writes the scheme and Restore puts back exactly what was there
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Squeaker\SelfTest", false);
            using (var k = Registry.CurrentUser.CreateSubKey(testKey))
            {
                k.SetValue("Arrow", @"%SystemRoot%\cursors\mine.cur", RegistryValueKind.ExpandString);
                k.SetValue("CursorBaseSize", 64, RegistryValueKind.DWord);
                k.SetValue("", "My Scheme");
            }
            var app = new Settings { Size = 48 };
            var root = Path.Combine(scratch, "Applied");
            var skipped = SystemCursors.Apply(app, testKey, root, notify: false);
            Check(skipped.Count == 0, "every role is applied");
            Check(SystemCursors.IsApplied(testKey), "Apply marks the scheme as Squeaker's");
            using (var k = Registry.CurrentUser.OpenSubKey(testKey)!)
            {
                Check(File.Exists((string)k.GetValue("Arrow")!), "the applied arrow file exists");
                Check((int)k.GetValue("CursorBaseSize")! == 48, "Apply sets the pointer size");
            }
            SystemCursors.Apply(app, testKey, root, notify: false);
            Check(Directory.GetDirectories(root).Length == 1, "applying again tidies the old files");
            SystemCursors.Restore(app, testKey, root, notify: false);
            using (var k = Registry.CurrentUser.OpenSubKey(testKey)!)
            {
                Check((string?)k.GetValue("Arrow", null, RegistryValueOptions.DoNotExpandEnvironmentNames) == @"%SystemRoot%\cursors\mine.cur", "Restore brings back the old arrow");
                Check((int)k.GetValue("CursorBaseSize")! == 64, "Restore brings back the old size");
                Check((string?)k.GetValue("") == "My Scheme", "Restore brings back the scheme name");
                Check(k.GetValue("Hand") == null, "Restore removes values that weren't there before");
            }
            Check(app.Backup == null && !Directory.Exists(root), "Restore clears the backup and the files");

            // Effects
            Check(ClickEffects.SqueakWav().Length > 10000, "the squeak sound is made");
            // The real overlay window, played far off-screen so nobody sees it.
            using (var effects = new ClickEffects())
            {
                effects.Update(new EffectSettings { Enabled = true });
                Check(effects.Watching, "the click watcher installs on its own thread");
                effects.Update(new EffectSettings { Enabled = false });
                Check(!effects.Watching, "the click watcher stops when effects are turned off");
                Check(SystemCursors.LiveSize >= 32, "Windows reports its live pointer size");
                foreach (var kind in ClickEffects.Kinds) effects.Play(-32000, -32000, false, new EffectSettings { Kind = kind, Duration = 200 });
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
            }
            foreach (var kind in ClickEffects.Kinds)
            {
                var still = Pix.FromBitmap(ClickEffects.Still(kind, "#0A84FF", 64, 0.4));
                Check(still.P.Where((_, i) => i % 4 == 3).Any(a => a > 40), $"the {kind} effect draws something");
            }
        }
        catch (Exception e) { fails.Add("crashed: " + e); }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Squeaker\SelfTest", false);
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
        File.WriteAllText(Log, fails.Count == 0 ? "All checks passed." : string.Join(Environment.NewLine, fails));
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>The first frame's .cur inside an .ani.</summary>
    static byte[] FirstIcon(byte[] ani)
    {
        int i = Encoding.ASCII.GetString(ani).IndexOf("icon", StringComparison.Ordinal);
        int len = BitConverter.ToInt32(ani, i + 4);
        return ani[(i + 8)..(i + 8 + len)];
    }

    /// <summary>`--snapshot dir`: renders every page, light and dark, to PNGs from a window that never appears on screen.</summary>
    public static int Snapshot(string dir)
    {
        Directory.CreateDirectory(dir);
        var w = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, Height = 1500 };
        w.Show();
        foreach (var dark in new[] { false, true })
        {
            App.ApplyTheme(dark);
            for (int page = 0; page < 4; page++)
            {
                w.ShowPage(page);
                w.RenderPreviews();
                // Let switch animations finish before the picture is taken.
                var frame = new DispatcherFrame();
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
                t.Start();
                Dispatcher.PushFrame(frame);
                w.UpdateLayout();
                var root = (FrameworkElement)w.Content;
                var rtb = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var f = File.Create(Path.Combine(dir, $"page{page}-{(dark ? "dark" : "light")}.png"));
                enc.Save(f);
            }
        }
        w.CloseQuietly();
        return 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadImage(IntPtr inst, string name, int type, int cx, int cy, int load);
    [DllImport("user32.dll")] static extern bool DestroyCursor(IntPtr h);
}
