using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Squeaker.Core;

/// <summary>Puts a look into Windows (and takes it back out), plus Windows' own pointer options.</summary>
public static class SystemCursors
{
    public const string CursorKey = @"Control Panel\Cursors";
    static readonly string[] ExtraValues = ["", "Scheme Source", "CursorBaseSize"];
    public static string AppliedRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Squeaker", "Applied");

    /// <summary>Builds every cursor, saves them and switches Windows to them. Returns roles that had nothing to build from.</summary>
    public static List<string> Apply(Settings s, string key = CursorKey, string? root = null, bool notify = true)
    {
        root ??= AppliedRoot;
        using var k = Registry.CurrentUser.CreateSubKey(key);
        s.Backup ??= Snapshot(k);

        // A fresh folder each time, so Windows never serves a cached copy of an older file.
        var dir = Path.Combine(root, DateTime.Now.Ticks.ToString());
        Directory.CreateDirectory(dir);
        var skipped = new List<string>();
        var paths = new List<string>();
        foreach (var role in Forge.Roles)
        {
            var built = Forge.Build(role, s);
            if (built == null) { skipped.Add(role.Name); paths.Add(""); continue; }
            var path = Path.Combine(dir, role.Reg + built.Ext);
            File.WriteAllBytes(path, built.File);
            k.SetValue(role.Reg, path, RegistryValueKind.ExpandString);
            paths.Add(path);
        }
        k.SetValue("", "Squeaker");
        k.SetValue("Scheme Source", 1, RegistryValueKind.DWord);
        k.SetValue("CursorBaseSize", s.Size, RegistryValueKind.DWord);
        using (var schemes = Registry.CurrentUser.CreateSubKey(key + @"\Schemes"))
            schemes.SetValue("Squeaker", string.Join(",", paths), RegistryValueKind.ExpandString);
        if (notify) Notify(s.Size);

        foreach (var old in Directory.GetDirectories(root).Where(d => !string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)))
            try { Directory.Delete(old, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return skipped;
    }

    /// <summary>Puts back exactly what was there before Squeaker's first Apply.</summary>
    public static void Restore(Settings s, string key = CursorKey, string? root = null, bool notify = true)
    {
        root ??= AppliedRoot;
        using var k = Registry.CurrentUser.CreateSubKey(key);
        var backup = s.Backup ?? DefaultScheme();
        foreach (var name in Forge.Roles.Select(r => r.Reg).Concat(ExtraValues))
        {
            if (!backup.TryGetValue(name, out var v)) { k.DeleteValue(name, false); continue; }
            if (v.StartsWith("dword:")) k.SetValue(name, int.Parse(v[6..]), RegistryValueKind.DWord);
            else k.SetValue(name, v, name == "" ? RegistryValueKind.String : RegistryValueKind.ExpandString);
        }
        using (var schemes = Registry.CurrentUser.CreateSubKey(key + @"\Schemes")) schemes.DeleteValue("Squeaker", false);
        s.Backup = null;
        if (notify) Notify(backup.TryGetValue("CursorBaseSize", out var b) && b.StartsWith("dword:") ? int.Parse(b[6..]) : 32);
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>True when Windows is currently showing a look Squeaker applied.</summary>
    public static bool IsApplied(string key = CursorKey)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        return k?.GetValue("") as string == "Squeaker";
    }

    static Dictionary<string, string> Snapshot(RegistryKey k)
    {
        var d = new Dictionary<string, string>();
        foreach (var name in Forge.Roles.Select(r => r.Reg).Concat(ExtraValues))
        {
            var v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (v is int i) d[name] = "dword:" + i;
            else if (v != null) d[name] = v.ToString()!;
        }
        return d;
    }

    /// <summary>Windows' own Aero scheme, used when there is no backup to go back to.</summary>
    static Dictionary<string, string> DefaultScheme()
    {
        var d = Forge.Roles.Where(r => r.Aero != null).ToDictionary(r => r.Reg, r => @"%SystemRoot%\cursors\" + r.Aero);
        d[""] = "Windows Default";
        d["Scheme Source"] = "dword:2";
        d["CursorBaseSize"] = "dword:32";
        return d;
    }

    static void Notify(int size)
    {
        // Keeps the Settings app's pointer-size slider in step with the size Squeaker chose.
        using (var acc = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Accessibility"))
            acc.SetValue("CursorSize", Math.Clamp((size - 32) / 16 + 1, 1, 15), RegistryValueKind.DWord);
        // The registry's CursorBaseSize is only read at sign-in; this (undocumented, used by the Settings app's
        // pointer-size slider) changes the live size. Then SPI_SETCURSORS reloads the files at that size.
        SystemParametersInfo(SPI_SETCURSORBASESIZE, 0, (IntPtr)size, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }

    // ---------- Windows' pointer options ----------

    public static int Speed
    {
        get { int v = 10; SystemParametersInfo(0x70, 0, ref v, 0); return v; }
        set => SystemParametersInfo(0x71, 0, Math.Clamp(value, 1, 20), SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }

    public static bool EnhancePrecision
    {
        get { var a = new int[3]; SystemParametersInfo(0x03, 0, a, 0); return a[2] != 0; }
        set { var a = value ? new[] { 6, 10, 1 } : new[] { 0, 0, 0 }; SystemParametersInfo(0x04, 0, a, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE); }
    }

    /// <summary>0 = off, otherwise 2..12 ghost pointers.</summary>
    public static int Trails
    {
        get { int v = 0; SystemParametersInfo(0x5E, 0, ref v, 0); return v < 2 ? 0 : v; }
        set => SystemParametersInfo(0x5D, value < 2 ? 0 : value, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }

    public static bool Sonar { get => GetBool(0x101C); set => SetBool(0x101D, value); }
    public static bool HideWhileTyping { get => GetBool(0x1020); set => SetBool(0x1021, value); }
    public static bool Shadow { get => GetBool(0x101A); set => SetBool(0x101B, value); }

    public static bool SnapToDefault
    {
        get { int v = 0; SystemParametersInfo(0x5F, 0, ref v, 0); return v != 0; }
        set => SystemParametersInfo(0x60, value ? 1 : 0, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }

    static bool GetBool(int action) { int v = 0; SystemParametersInfo(action, 0, ref v, 0); return v != 0; }
    static void SetBool(int action, bool v) => SystemParametersInfo(action, 0, v ? 1 : 0, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

    // ---------- start with Windows ----------

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetStartup(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("Squeaker", $"\"{Environment.ProcessPath}\" --tray");
        else k.DeleteValue("Squeaker", false);
    }

    const int SPI_SETCURSORS = 0x57, SPI_GETCURSORBASESIZE = 0x2028, SPI_SETCURSORBASESIZE = 0x2029, SPIF_UPDATEINIFILE = 1, SPIF_SENDCHANGE = 2;

    /// <summary>The pointer size Windows is using right now.</summary>
    public static int LiveSize { get { int v = 32; SystemParametersInfo(SPI_GETCURSORBASESIZE, 0, ref v, 0); return v; } }

    [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(int action, int param, IntPtr pv, int winIni);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(int action, int param, ref int pv, int winIni);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(int action, int param, int[] pv, int winIni);
    static void SystemParametersInfo(int action, int param, int pvValue, int winIni) => SystemParametersInfo(action, param, (IntPtr)pvValue, winIni);
}
