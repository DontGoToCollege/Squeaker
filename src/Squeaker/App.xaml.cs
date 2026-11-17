using System.Threading;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Squeaker.Core;
using Forms = System.Windows.Forms;

namespace Squeaker;

public partial class App : Application
{
    public static string Version => typeof(App).Assembly.GetName().Version!.ToString(3);
    public static Settings Settings { get; private set; } = new();
    public static ClickEffects Effects { get; } = new();

    Mutex? single;
    Forms.NotifyIcon? tray;
    Forms.ToolStripMenuItem? trayEffects;
    MainWindow? window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        ApplyTheme();

        if (args.Contains("--selftest")) { Shutdown(SelfTest.Run()); return; }
        Settings = Settings.Load();
        if (args.Length >= 2 && args[0] == "--snapshot") { Shutdown(SelfTest.Snapshot(args[1])); return; }

        single = new Mutex(true, @"Local\Squeaker-Single", out bool first);
        if (!first)
        {
            // Already running: wake that copy up instead of starting a second one.
            try { EventWaitHandle.OpenExisting(@"Local\Squeaker-Show").Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Squeaker-Show");
        new Thread(() => { while (show.WaitOne()) Dispatcher.BeginInvoke(new Action(ShowMain)); }) { IsBackground = true }.Start();

        Effects.Update(Settings.Effects);
        SystemEvents.UserPreferenceChanged += (_, ev) => { if (ev.Category == UserPreferenceCategory.General) Dispatcher.BeginInvoke(new Action(ApplyTheme)); };
        BuildTray();
        if (!args.Contains("--tray")) ShowMain();
    }

    public void ShowMain()
    {
        window ??= new MainWindow();
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>Closing the window keeps Squeaker in the tray only while click effects need it.</summary>
    public bool KeepRunningInTray => Settings.Effects.Enabled;

    public void Quit()
    {
        Effects.Dispose();
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        Shutdown();
    }

    public void SyncTray()
    {
        if (trayEffects != null) trayEffects.Checked = Settings.Effects.Enabled;
    }

    void BuildTray()
    {
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Squeaker", null, (_, _) => ShowMain());
        trayEffects = new Forms.ToolStripMenuItem("Click effects", null, (_, _) =>
        {
            Settings.Effects.Enabled = !Settings.Effects.Enabled;
            Effects.Update(Settings.Effects);
            Settings.Save();
            SyncTray();
            window?.Refresh();
        }) { Checked = Settings.Effects.Enabled };
        menu.Items.Add(trayEffects);
        menu.Items.Add("Restore Windows cursors", null, (_, _) =>
        {
            SystemCursors.Restore(Settings);
            Settings.Save();
            window?.Refresh();
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Squeaker", null, (_, _) => Quit());
        tray = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(iconStream, 16, 16), Text = "Squeaker", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, ev) => { if (ev.Button == Forms.MouseButtons.Left) ShowMain(); };
    }

    public static bool IsDark()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    /// <summary>Light or dark to match Windows; switches live when the user changes it.</summary>
    public static void ApplyTheme() => ApplyTheme(IsDark());

    public static void ApplyTheme(bool dark)
    {
        var r = Current.Resources;
        void Set(string key, string light, string darkHex) => r[key] = Freeze(new SolidColorBrush(Forge.Parse(dark ? darkHex : light)));
        Set("Bg", "#F5F5F7", "#1E1E20");
        Set("Sidebar", "#EBEBEE", "#26262A");
        Set("Card", "#FFFFFF", "#2C2C2F");
        Set("CardBorder", "#E3E3E8", "#3A3A3E");
        Set("Control", "#FFFFFF", "#38383C");
        Set("Text", "#1D1D1F", "#F5F5F7");
        Set("Text2", "#6E6E73", "#A1A1A6");
        Set("Text3", "#AEAEB2", "#6C6C70");
        Set("Separator", "#ECECF0", "#38383C");
        Set("Hover", "#0F000000", "#14FFFFFF");
        Set("Selected", "#1A0A84FF", "#330A84FF");
        Set("Accent", "#007AFF", "#0A84FF");
        Set("Green", "#34C759", "#30D158");
        Set("SwitchOff", "#E3E3E8", "#48484C");
        Set("Danger", "#D70015", "#FF453A");
        r["Stage"] = Freeze(new LinearGradientBrush(Forge.Parse(dark ? "#2A2D34" : "#F2F5FA"), Forge.Parse(dark ? "#1F2126" : "#E2E7EF"), 90));
        r["Checker"] = Checker(dark);
    }

    static Brush Freeze(Brush b) { b.Freeze(); return b; }

    /// <summary>The transparent-picture checkerboard behind cursor previews.</summary>
    static Brush Checker(bool dark)
    {
        var a = new SolidColorBrush(Forge.Parse(dark ? "#2E2E32" : "#FFFFFF"));
        var b = new SolidColorBrush(Forge.Parse(dark ? "#37373B" : "#F0F0F3"));
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(a, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        g.Children.Add(new GeometryDrawing(b, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        g.Children.Add(new GeometryDrawing(b, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        return Freeze(new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute });
    }
}
