using System.IO;
using System.Text.Json;

namespace Squeaker.Core;

/// <summary>Everything Squeaker remembers, saved as JSON in %AppData%\Squeaker.</summary>
public sealed class Settings
{
    public string Style { get; set; } = "Classic";
    public string FillTop { get; set; } = "#FFFFFF";
    public string FillBottom { get; set; } = "#FFFFFF";
    public string Outline { get; set; } = "#000000";
    /// <summary>Colour for the coloured parts (the busy ring); null keeps Windows' blue.</summary>
    public string? Accent { get; set; }
    public int Size { get; set; } = 32;
    public Dictionary<string, CustomCursor> Custom { get; set; } = [];
    public EffectSettings Effects { get; set; } = new();
    /// <summary>The user's cursor registry values from before Squeaker first applied a look.</summary>
    public Dictionary<string, string>? Backup { get; set; }
    public bool StartWithWindows { get; set; }

    // SQUEAKER_HOME lets the self-test work in a scratch folder instead of the real settings.
    public static string Folder => Environment.GetEnvironmentVariable("SQUEAKER_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Squeaker");
    public static string CustomFolder => Path.Combine(Folder, "Custom");
    static string FilePath => Path.Combine(Folder, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, true);
    }

    public Settings Copy() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this))!;
}

/// <summary>A picture or cursor file the user chose for one role.</summary>
public sealed class CustomCursor
{
    public string File { get; set; } = "";
    /// <summary>Hotspot as a fraction of the picture (0..1); null uses the file's own or the role's default.</summary>
    public double? HotX { get; set; }
    public double? HotY { get; set; }
    /// <summary>Recolour it with the current style.</summary>
    public bool Tint { get; set; }
}

public sealed class EffectSettings
{
    public bool Enabled { get; set; }
    public string Kind { get; set; } = "Ripple";
    public string LeftColor { get; set; } = "#0A84FF";
    public string RightColor { get; set; } = "#FF375F";
    public bool RightClicks { get; set; } = true;
    public int Size { get; set; } = 56;
    public int Duration { get; set; } = 450;
    public bool Squeak { get; set; }
}
