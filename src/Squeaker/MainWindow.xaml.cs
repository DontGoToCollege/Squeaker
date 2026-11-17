using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Squeaker.Core;

namespace Squeaker;

public partial class MainWindow : Window
{
    static Settings S => App.Settings;

    static readonly (int Size, string Name)[] SizePresets = [(32, "Standard"), (48, "Large"), (64, "Larger"), (96, "Huge"), (128, "Giant")];
    static readonly string[] PaletteColors =
    [
        "#FFFFFF", "#E5E5EA", "#AEAEB2", "#636366", "#2C2C2E", "#000000",
        "#FF3B30", "#FF9500", "#FFCC00", "#34C759", "#00C7BE", "#32ADE6",
        "#007AFF", "#5856D6", "#AF52DE", "#FF2D55", "#A2845E", "#FFB3C7",
        "#FFD60A", "#FF9F0A", "#64D2FF", "#BF5AF2", "#A6F4C5", "#0B1F3A",
    ];

    bool loading = true, dirty, quitting;   // loading until the constructor's first Refresh: XAML fires change events while it builds
    readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    readonly Dictionary<string, Cursor?> tryCursors = [];
    readonly Dictionary<string, Image> stripImages = [], listImages = [];
    readonly Dictionary<string, TextBlock> listBadges = [];
    readonly Dictionary<string, RadioButton> styleTiles = [], effectTiles = [];
    readonly List<RadioButton> sizeChips = [];
    Image? customStyleImage;
    Popup? palette;
    Action<string>? paletteTarget;

    public MainWindow()
    {
        InitializeComponent();
        AppIcon.Source = Icon = LoadIcon();
        VersionText.Text = "Squeaker " + App.Version;
        previewTimer.Tick += (_, _) => { previewTimer.Stop(); RenderPreviews(); };
        StateChanged += (_, _) =>
        {
            // A maximized custom-chrome window hangs a few pixels off each screen edge.
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        };
        Stage.MouseEnter += (_, _) => Stage.Cursor = TryCursor("Arrow");

        BuildStyleTiles();
        BuildSizeChips();
        BuildRoles();
        BuildEffectTiles();
        Refresh();
        RenderPreviews();
        RoleList.SelectedIndex = 0;
    }

    static BitmapSource LoadIcon()
    {
        var dec = new IconBitmapDecoder(Application.GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream,
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return dec.Frames.OrderByDescending(f => f.PixelWidth).First();
    }

    // ================= state =================

    /// <summary>Puts every control in step with the settings (and Windows' own pointer options).</summary>
    public void Refresh()
    {
        loading = true;
        foreach (var (name, tile) in styleTiles) tile.IsChecked = name == S.Style;
        SetWell(FillTopWell, S.FillTop);
        SetWell(FillBottomWell, S.FillBottom);
        SetWell(OutlineWell, S.Outline);
        SetWell(AccentWell, S.Accent ?? "#0078D7");
        AccentReset.Visibility = S.Accent == null ? Visibility.Collapsed : Visibility.Visible;
        SizeSlider.Value = S.Size;
        SyncSize();

        var fx = S.Effects;
        EffectsSwitch.IsChecked = fx.Enabled;
        foreach (var (kind, tile) in effectTiles) tile.IsChecked = kind == fx.Kind;
        SetWell(LeftWell, fx.LeftColor);
        SetWell(RightWell, fx.RightColor);
        RightSwitch.IsChecked = fx.RightClicks;
        SqueakSwitch.IsChecked = fx.Squeak;
        EffectSize.Value = fx.Size;
        EffectDuration.Value = fx.Duration;
        EffectSizeText.Text = $"{fx.Size} pt";
        EffectDurationText.Text = $"{fx.Duration / 1000.0:0.00} s";

        SpeedSlider.Value = SystemCursors.Speed;
        PrecisionSwitch.IsChecked = SystemCursors.EnhancePrecision;
        SnapSwitch.IsChecked = SystemCursors.SnapToDefault;
        int trails = SystemCursors.Trails;
        TrailsSwitch.IsChecked = trails > 0;
        TrailsSlider.Value = trails > 0 ? trails : 7;
        TrailsRow.Visibility = trails > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShadowSwitch.IsChecked = SystemCursors.Shadow;
        SonarSwitch.IsChecked = SystemCursors.Sonar;
        VanishSwitch.IsChecked = SystemCursors.HideWhileTyping;
        StartupSwitch.IsChecked = S.StartWithWindows;
        loading = false;
        UpdateStatus();
    }

    /// <summary>The look changed: remember it, mark it unapplied and redraw the previews shortly.</summary>
    void Changed()
    {
        if (loading) return;
        dirty = true;
        S.Save();
        tryCursors.Clear();
        previewTimer.Stop();
        previewTimer.Start();
        UpdateStatus();
    }

    void UpdateStatus()
    {
        bool applied = SystemCursors.IsApplied();
        StatusDot.Fill = applied ? (Brush)FindResource("Green") : (Brush)FindResource("Text3");
        StatusText.Text = applied ? "Squeaker look is on" : "Windows cursors";
        StatusDetail.Text = (applied ? "Applied across Windows" : "Nothing applied yet") + (S.Effects.Enabled ? "\nClick effects on" : "");
        // Squeaker 1.0.0 saved the size without making Windows use it, so check what Windows really shows.
        bool sizeOff = applied && SystemCursors.LiveSize != S.Size;
        DirtyDot.Visibility = dirty || !applied || sizeOff ? Visibility.Visible : Visibility.Collapsed;
        ActionHint.Text = dirty ? "You have changes that aren't applied yet."
            : sizeOff ? $"Windows is showing a {SystemCursors.LiveSize} px pointer, not {S.Size} px. Apply again to fix it."
            : applied ? "Windows is using your Squeaker look."
            : "Windows is using its own cursors. Apply to switch to this look.";

        bool fx = S.Effects.Enabled;
        StageHint.Text = fx ? "Move your pointer here to try it, and click to see your click effect"
                            : "Move your pointer here to try it. Click effects are off: turn them on under Click Effects";
        TryHint.Text = fx ? "Click anywhere in here" : "Click anywhere in here to preview.\nTurn on “Show click effects” above to see them in every app.";
    }

    // ================= previews =================

    double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    public void RenderPreviews()
    {
        // Icons are drawn at a fixed, crisp size; the stage shows the real size Windows will use on this screen.
        var icons = S.Copy();
        icons.Size = 64;
        foreach (var role in Forge.Roles)
        {
            var built = Forge.Build(role, icons, previewOnly: true);
            var img = built == null ? null : Trim(built.Preview);
            stripImages[role.Reg].Source = img;
            listImages[role.Reg].Source = img;
            listBadges[role.Reg].Text = built == null ? "Unavailable" : S.Custom.ContainsKey(role.Reg) ? "Your picture" : "Style";
        }
        var real = S.Copy();
        real.Size = Math.Min(256, (int)Math.Round(S.Size * PixelsPerDip));
        if (Forge.Build(Forge.Role("Arrow"), real, previewOnly: true) is { } arrow)
        {
            var img = Trim(arrow.Preview);
            StageArrow.Source = img;
            StageArrow.Width = img.PixelWidth / PixelsPerDip;
            StageArrow.Height = img.PixelHeight / PixelsPerDip;
        }
        StageSize.Text = $"{S.Size} px";
        StageStyle.Text = (S.Style == "Custom" ? "Custom colors" : S.Style) + " · actual size";
        if (customStyleImage != null) customStyleImage.Source = StyleThumb(S);
        UpdateRoleDetail();
    }

    /// <summary>The cursor itself, at the size Windows would show it here, for trying out before applying.</summary>
    Cursor? TryCursor(string reg)
    {
        if (tryCursors.TryGetValue(reg, out var c)) return c;
        int px = Math.Min(256, (int)Math.Round(S.Size * PixelsPerDip));
        var built = Forge.Build(Forge.Role(reg), S, only: [px]);
        c = built == null ? null : new Cursor(new MemoryStream(built.File), false);
        tryCursors[reg] = c;
        return c;
    }

    /// <summary>Crops away the transparent margin so small previews are centred on the pointer itself.</summary>
    static BitmapSource Trim(BitmapSource src)
    {
        var p = Pix.FromBitmap(src);
        int x0 = p.W, y0 = p.H, x1 = -1, y1 = -1;
        for (int y = 0; y < p.H; y++)
            for (int x = 0; x < p.W; x++)
                if (p.P[(y * p.W + x) * 4 + 3] > 8) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        if (x1 < 0) return src;
        var r = new CroppedBitmap(src, new Int32Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1));
        r.Freeze();
        return r;
    }

    static BitmapSource? StyleThumb(Settings colors)
    {
        var s = new Settings { FillTop = colors.FillTop, FillBottom = colors.FillBottom, Outline = colors.Outline, Accent = colors.Accent, Size = 64 };
        var b = Forge.Build(Forge.Role("Arrow"), s, previewOnly: true);
        return b == null ? null : Trim(b.Preview);
    }

    // ================= appearance =================

    void BuildStyleTiles()
    {
        foreach (var st in Forge.Styles.Select(x => (x.Name, Thumb: StyleThumb(new Settings { FillTop = x.FillTop, FillBottom = x.FillBottom, Outline = x.Outline, Accent = x.Accent }))).Append(("Custom", null)))
        {
            var image = new Image { Source = st.Thumb, Height = 34, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            if (st.Name == "Custom") customStyleImage = image;
            var tile = new RadioButton
            {
                Style = (Style)FindResource("Tile"), GroupName = "style", Margin = new Thickness(2),
                Content = new StackPanel
                {
                    Width = 76,
                    Children =
                    {
                        new Border { Height = 56, CornerRadius = new CornerRadius(8), Background = (Brush)FindResource("Stage"), Child = image },
                        new TextBlock { Text = st.Name, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), FontSize = 12 },
                    },
                },
            };
            ((Border)((StackPanel)tile.Content).Children[0]).SetResourceReference(Border.BackgroundProperty, "Stage");
            var name = st.Name;
            tile.Checked += (_, _) => PickStyle(name);
            styleTiles[name] = tile;
            StylePanel.Children.Add(tile);
        }
    }

    void PickStyle(string name)
    {
        if (loading || name == S.Style) return;
        S.Style = name;
        if (Forge.Styles.FirstOrDefault(x => x.Name == name) is { } st)
            (S.FillTop, S.FillBottom, S.Outline, S.Accent) = (st.FillTop, st.FillBottom, st.Outline, st.Accent);
        Refresh();
        Changed();
    }

    void BuildSizeChips()
    {
        foreach (var (size, name) in SizePresets)
        {
            var chip = new RadioButton { Style = (Style)FindResource("Chip"), GroupName = "size", Content = name, Tag = size };
            chip.Checked += (_, _) => { if (!loading) SizeSlider.Value = size; };
            sizeChips.Add(chip);
            SizeChips.Children.Add(chip);
        }
    }

    void SyncSize()
    {
        SizeValue.Text = $"{S.Size} px";
        bool was = loading;
        loading = true;
        foreach (var c in sizeChips) c.IsChecked = (int)c.Tag == S.Size;
        loading = was;
    }

    void SizeSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading || SizeValue == null) return;
        S.Size = (int)SizeSlider.Value;
        SyncSize();
        Changed();
    }

    static void SetWell(Button well, string hex) => well.Background = new SolidColorBrush(Forge.Parse(hex));

    void Well_Click(object sender, RoutedEventArgs e)
    {
        var well = (Button)sender;
        var key = (string)well.Tag;
        OpenPalette(well, hex =>
        {
            switch (key)
            {
                case "FillTop": S.FillTop = hex; break;
                case "FillBottom": S.FillBottom = hex; break;
                case "Outline": S.Outline = hex; break;
                case "Accent": S.Accent = hex; break;
                case "LeftColor": S.Effects.LeftColor = hex; SaveEffects(); RefreshEffectTiles(); Refresh(); return;
                case "RightColor": S.Effects.RightColor = hex; SaveEffects(); Refresh(); return;
            }
            S.Style = "Custom";
            Refresh();
            Changed();
        });
    }

    void AccentReset_Click(object sender, RoutedEventArgs e)
    {
        S.Accent = null;
        S.Style = "Custom";
        Refresh();
        Changed();
    }

    void OpenPalette(Button anchor, Action<string> pick)
    {
        if (palette == null)
        {
            var grid = new UniformGrid { Columns = 6 };
            foreach (var hex in PaletteColors)
            {
                var sw = new Button { Style = (Style)FindResource("Swatch"), Background = new SolidColorBrush(Forge.Parse(hex)), ToolTip = hex };
                sw.Click += (_, _) => { palette!.IsOpen = false; paletteTarget?.Invoke(hex); };
                grid.Children.Add(sw);
            }
            var more = new Button { Style = (Style)FindResource("Link"), Content = "Other Color…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            more.Click += (_, _) =>
            {
                palette!.IsOpen = false;
                using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    paletteTarget?.Invoke($"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}");
            };
            var card = new Border { Style = (Style)FindResource("CardBox"), Padding = new Thickness(10), Margin = new Thickness(10), Child = new StackPanel { Children = { grid, more } } };
            palette = new Popup { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PopupAnimation = PopupAnimation.Fade, Child = card, HorizontalOffset = -10 };
        }
        paletteTarget = pick;
        palette.PlacementTarget = anchor;
        palette.IsOpen = true;
    }

    void Stage_Click(object sender, MouseButtonEventArgs e) => PlayAt(e, e.ChangedButton == MouseButton.Right);

    void PlayAt(MouseEventArgs e, bool right)
    {
        var p = PointToScreen(e.GetPosition(this));
        App.Effects.Play((int)p.X, (int)p.Y, right, S.Effects);
    }

    // ================= custom cursors =================

    void BuildRoles()
    {
        foreach (var role in Forge.Roles)
        {
            var strip = new Image { Width = 30, Height = 30, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(strip, BitmapScalingMode.HighQuality);
            var tile = new Border { Width = 50, Height = 50, CornerRadius = new CornerRadius(8), Child = strip, ToolTip = role.Name, Margin = new Thickness(2), BorderThickness = new Thickness(1) };
            tile.SetResourceReference(Border.BackgroundProperty, "Stage");
            var reg = role.Reg;
            tile.MouseEnter += (_, _) => { tile.Cursor = TryCursor(reg); tile.SetResourceReference(Border.BorderBrushProperty, "Accent"); };
            tile.MouseLeave += (_, _) => tile.BorderBrush = Brushes.Transparent;
            tile.MouseLeftButtonDown += (_, _) => { RoleList.SelectedIndex = Array.IndexOf(Forge.Roles, role); NavCustom.IsChecked = true; };
            stripImages[reg] = strip;
            RoleStrip.Items.Add(tile);

            var icon = new Image { Width = 24, Height = 24, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            var badge = new TextBlock { FontSize = 11.5 };
            badge.SetResourceReference(TextBlock.ForegroundProperty, "Text2");
            var well = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(8), Child = icon };
            well.SetResourceReference(Border.BackgroundProperty, "Stage");
            RoleList.Items.Add(new ListBoxItem
            {
                Tag = reg,
                Content = new DockPanel
                {
                    Children =
                    {
                        well,
                        new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                            Children = { new TextBlock { Text = role.Name, FontSize = 13 }, badge } },
                    },
                },
            });
            listImages[reg] = icon;
            listBadges[reg] = badge;
        }
    }

    string SelectedReg => (RoleList.SelectedItem as ListBoxItem)?.Tag as string ?? "Arrow";

    void RoleList_Changed(object sender, SelectionChangedEventArgs e) => UpdateRoleDetail();

    void UpdateRoleDetail()
    {
        if (RoleTitle == null) return;
        var role = Forge.Role(SelectedReg);
        S.Custom.TryGetValue(role.Reg, out var custom);
        RoleTitle.Text = role.Name;
        var big = S.Copy();
        big.Size = 160;
        var built = Forge.Build(role, big, previewOnly: true);
        RolePreview.Source = built?.Preview;
        RoleSource.Text = built == null ? "Windows has no picture for this pointer to start from. Choose your own."
            : custom != null ? (built.Animated ? "Your animated cursor" : "Your picture")
            : S.Style == "Custom" ? "From your custom colors" : $"From the {S.Style} style";
        HotMark.Visibility = built == null ? Visibility.Collapsed : Visibility.Visible;
        if (built != null)
        {
            RolePreview.UpdateLayout();
            double w = RolePreview.ActualWidth > 0 ? RolePreview.ActualWidth : 158;
            Canvas.SetLeft(HotMark, built.HotX * w - 9);
            Canvas.SetTop(HotMark, built.HotY * w - 9);
        }
        HotHint.Text = custom != null ? "Click the picture to set the hotspot, the exact point that clicks." : "The ring marks the hotspot, the exact point that clicks.";
        HotBox.Cursor = custom != null ? Cursors.Cross : null;
        TintSwitch.IsChecked = custom?.Tint == true;
        TintSwitch.IsEnabled = custom != null;
        UseStyleButton.IsEnabled = custom != null;
    }

    void HotBox_Click(object sender, MouseButtonEventArgs e)
    {
        if (!S.Custom.TryGetValue(SelectedReg, out var custom) || RolePreview.ActualWidth <= 0) return;
        var p = e.GetPosition(RolePreview);
        try { (custom.HotX, custom.HotY) = Forge.PreviewToSource(custom, p.X / RolePreview.ActualWidth, p.Y / RolePreview.ActualHeight); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { Status(ex.Message, true); return; }
        Changed();
        UpdateRoleDetail();
    }

    void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose a picture or cursor for “{Forge.Role(SelectedReg).Name}”",
            Filter = "Pictures and cursors|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.ico;*.cur;*.ani;*.webp|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dlg.FileName).Length > CurIO.MaxBytes) throw new InvalidDataException("That file is bigger than 20 MB.");
            SetCustom(SelectedReg, File.ReadAllBytes(dlg.FileName), Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { Status(ex.Message, true); }
    }

    void SetCustom(string reg, byte[] data, string name)
    {
        S.Custom[reg] = Importer.SaveForRole(reg, data);
        Status($"{Forge.Role(reg).Name} now uses {name}.", false);
        Changed();
        RenderPreviews();
    }

    void UseStyle_Click(object sender, RoutedEventArgs e)
    {
        S.Custom.Remove(SelectedReg);
        Changed();
        RenderPreviews();
    }

    void Tint_Click(object sender, RoutedEventArgs e)
    {
        if (!S.Custom.TryGetValue(SelectedReg, out var custom)) return;
        custom.Tint = TintSwitch.IsChecked == true;
        Changed();
        RenderPreviews();
    }

    void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        S.Custom.Clear();
        Status("Every pointer follows the style again.", false);
        Changed();
        RenderPreviews();
    }

    void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ImportUrl_Click(sender, e);
    }

    async void ImportUrl_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UrlBox.Text)) { Status("Paste a link first.", true); return; }
        ImportUrlButton.IsEnabled = false;
        Status("Downloading…", false);
        try
        {
            var (data, name) = await Importer.DownloadAsync(UrlBox.Text);
            if (CurIO.Sniff(data) == "zip") MergePack(Importer.ImportPack(data), name);
            else SetCustom(SelectedReg, data, string.IsNullOrEmpty(name) ? "the downloaded file" : name);
            UrlBox.Text = "";
        }
        catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or TaskCanceledException or IOException)
        {
            Status(ex is TaskCanceledException ? "The download took too long." : ex.Message, true);
        }
        finally { ImportUrlButton.IsEnabled = true; }
    }

    void ImportPackFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a cursor pack", Filter = "Cursor packs (.zip, install.inf)|*.zip;*.inf" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var pack = dlg.FileName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)
                ? Importer.ImportPack(null, Path.GetDirectoryName(dlg.FileName))
                : Importer.ImportPack(File.ReadAllBytes(dlg.FileName));
            MergePack(pack, Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { Status(ex.Message, true); }
    }

    void MergePack(Dictionary<string, CustomCursor> pack, string name)
    {
        foreach (var (reg, c) in pack) S.Custom[reg] = c;
        Status($"Imported {pack.Count} pointer{(pack.Count == 1 ? "" : "s")} from {name}.", false);
        Changed();
        RenderPreviews();
    }

    void Browse_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://www.rw-designer.com/cursor-library") { UseShellExecute = true });

    void Status(string text, bool error)
    {
        ImportStatus.Text = text;
        ImportStatus.Visibility = Visibility.Visible;
        ImportStatus.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "Text2");
    }

    // ================= click effects =================

    void BuildEffectTiles()
    {
        foreach (var kind in ClickEffects.Kinds)
        {
            var image = new Image { Width = 56, Height = 56 };
            var stage = new Border { Height = 64, CornerRadius = new CornerRadius(8), Child = image };
            stage.SetResourceReference(Border.BackgroundProperty, "Stage");
            var tile = new RadioButton
            {
                Style = (Style)FindResource("Tile"), GroupName = "fx", Margin = new Thickness(2), Tag = image,
                Content = new StackPanel { Width = 92, Children = { stage, new TextBlock { Text = kind, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) } } },
            };
            tile.Checked += (_, _) =>
            {
                if (loading) return;
                S.Effects.Kind = kind;
                // Picking an effect means wanting it: switch effects on rather than leave them silently off.
                S.Effects.Enabled = true;
                EffectsSwitch.IsChecked = true;
                SaveEffects();
                var p = image.PointToScreen(new Point(image.ActualWidth / 2, image.ActualHeight / 2));
                App.Effects.Play((int)p.X, (int)p.Y, false, S.Effects);
            };
            effectTiles[kind] = tile;
            EffectKinds.Children.Add(tile);
        }
        RefreshEffectTiles();
    }

    void RefreshEffectTiles()
    {
        foreach (var (kind, tile) in effectTiles) ((Image)tile.Tag).Source = ClickEffects.Still(kind, S.Effects.LeftColor, 112, 0.42);
    }

    void SaveEffects()
    {
        App.Effects.Update(S.Effects);
        S.Save();
        ((App)Application.Current).SyncTray();
        UpdateStatus();
    }

    void EffectsSwitch_Click(object sender, RoutedEventArgs e)
    {
        S.Effects.Enabled = EffectsSwitch.IsChecked == true;
        SaveEffects();
    }

    void EffectOption_Click(object sender, RoutedEventArgs e)
    {
        S.Effects.RightClicks = RightSwitch.IsChecked == true;
        S.Effects.Squeak = SqueakSwitch.IsChecked == true;
        SaveEffects();
    }

    void EffectSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading || EffectSizeText == null || EffectDurationText == null) return;
        S.Effects.Size = (int)EffectSize.Value;
        S.Effects.Duration = (int)EffectDuration.Value;
        EffectSizeText.Text = $"{S.Effects.Size} pt";
        EffectDurationText.Text = $"{S.Effects.Duration / 1000.0:0.00} s";
        SaveEffects();
    }

    void TryPad_Click(object sender, MouseButtonEventArgs e) => PlayAt(e, e.ChangedButton == MouseButton.Right);

    // ================= pointer options =================

    void Pointer_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        if (sender == SpeedSlider) SystemCursors.Speed = (int)SpeedSlider.Value;
        else if (sender == PrecisionSwitch) SystemCursors.EnhancePrecision = PrecisionSwitch.IsChecked == true;
        else if (sender == SnapSwitch) SystemCursors.SnapToDefault = SnapSwitch.IsChecked == true;
        else if (sender == ShadowSwitch) SystemCursors.Shadow = ShadowSwitch.IsChecked == true;
        else if (sender == SonarSwitch) SystemCursors.Sonar = SonarSwitch.IsChecked == true;
        else if (sender == VanishSwitch) SystemCursors.HideWhileTyping = VanishSwitch.IsChecked == true;
        else if (sender == TrailsSwitch || sender == TrailsSlider)
        {
            bool on = TrailsSwitch.IsChecked == true;
            SystemCursors.Trails = on ? (int)TrailsSlider.Value : 0;
            TrailsRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    void Startup_Click(object sender, RoutedEventArgs e)
    {
        S.StartWithWindows = StartupSwitch.IsChecked == true;
        SystemCursors.SetStartup(S.StartWithWindows);
        S.Save();
    }

    // ================= apply / restore =================

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        ApplyButton.IsEnabled = false;
        ApplyButton.Content = "Applying…";
        await Dispatcher.Yield(DispatcherPriority.Background);   // lets "Applying…" show before the work starts
        try
        {
            var skipped = SystemCursors.Apply(S);
            dirty = false;
            S.Save();
            UpdateStatus();
            if (skipped.Count > 0) ActionHint.Text = "Applied. Windows had nothing to start from for: " + string.Join(", ", skipped) + ".";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ActionHint.Text = "Couldn't apply: " + ex.Message;
        }
        finally
        {
            ApplyButton.IsEnabled = true;
            ApplyButton.Content = "Apply";
        }
    }

    void Restore_Click(object sender, RoutedEventArgs e)
    {
        SystemCursors.Restore(S);
        dirty = false;
        S.Save();
        UpdateStatus();
    }

    // ================= window =================

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageLook == null) return;
        PageLook.Visibility = sender == NavLook ? Visibility.Visible : Visibility.Collapsed;
        PageCustom.Visibility = sender == NavCustom ? Visibility.Visible : Visibility.Collapsed;
        PageEffects.Visibility = sender == NavEffects ? Visibility.Visible : Visibility.Collapsed;
        PagePointer.Visibility = sender == NavPointer ? Visibility.Visible : Visibility.Collapsed;
        ActionBar.Visibility = sender == NavLook || sender == NavCustom ? Visibility.Visible : Visibility.Collapsed;
        Scroller.ScrollToTop();
        if (sender == NavPointer) Refresh();   // Windows' options may have changed elsewhere
    }

    public void CloseQuietly() { quitting = true; Close(); }

    public void ShowPage(int i) => new[] { NavLook, NavCustom, NavEffects, NavPointer }[i].IsChecked = true;

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (quitting || Application.Current is not App app) return;
        if (app.KeepRunningInTray) { e.Cancel = true; Hide(); return; }
        quitting = true;
        app.Quit();
    }
}
