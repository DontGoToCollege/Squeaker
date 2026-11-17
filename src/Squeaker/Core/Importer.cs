using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace Squeaker.Core;

/// <summary>Brings pictures, cursor files and whole cursor packs (zip + install.inf) into Squeaker's own folder.</summary>
public static class Importer
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static Importer() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Squeaker/1.0");

    /// <summary>Downloads a file of at most 20 MB from an http(s) link.</summary>
    public static async Task<(byte[] Data, string Name)> DownloadAsync(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            throw new InvalidDataException("That doesn't look like a web link. It should start with https://");
        using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode) throw new InvalidDataException($"The site answered {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        if (resp.Content.Headers.ContentLength > CurIO.MaxBytes) throw new InvalidDataException("That file is bigger than 20 MB.");
        await using var s = await resp.Content.ReadAsStreamAsync();
        var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > CurIO.MaxBytes) throw new InvalidDataException("That file is bigger than 20 MB.");
        }
        var name = resp.Content.Headers.ContentDisposition?.FileNameStar ?? resp.Content.Headers.ContentDisposition?.FileName
                   ?? Path.GetFileName(uri.LocalPath);
        return (ms.ToArray(), name.Trim('"'));
    }

    /// <summary>Saves one picture or cursor for a role. Throws InvalidDataException if it can't be used.</summary>
    public static CustomCursor SaveForRole(string reg, byte[] data)
    {
        var kind = CurIO.Sniff(data);
        if (!CurIO.IsCursorData(data)) throw new InvalidDataException("That isn't a picture or cursor file Squeaker can use.");
        CurIO.Load(data);   // proves it decodes before anything is saved
        Directory.CreateDirectory(Settings.CustomFolder);
        var path = Path.Combine(Settings.CustomFolder, $"{reg}-{DateTime.Now.Ticks}.{kind}");
        File.WriteAllBytes(path, data);
        return new CustomCursor { File = path };
    }

    /// <summary>A cursor pack from a .zip (bytes) or a folder; returns the roles it filled.</summary>
    public static Dictionary<string, CustomCursor> ImportPack(byte[]? zip, string? folder = null)
    {
        var files = new Dictionary<string, Func<byte[]>>(StringComparer.OrdinalIgnoreCase);
        ZipArchive? archive = null;
        if (zip != null)
        {
            archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            // Read straight from the archive by name only; nothing is ever extracted to a path from the zip.
            foreach (var e in archive.Entries.Where(e => e.Length is > 0 and < CurIO.MaxBytes))
                files.TryAdd(e.Name, () => { using var s = e.Open(); var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); });
        }
        else if (folder != null)
            foreach (var f in Directory.GetFiles(folder).Where(f => new FileInfo(f).Length < CurIO.MaxBytes))
                files.TryAdd(Path.GetFileName(f), () => File.ReadAllBytes(f));

        try
        {
            var map = new Dictionary<string, string>();   // role -> file name
            var inf = files.Keys.FirstOrDefault(k => k.EndsWith(".inf", StringComparison.OrdinalIgnoreCase));
            if (inf != null && SchemeFromInf(new StreamReader(new MemoryStream(files[inf]()), Encoding.UTF8, true).ReadToEnd()) is { } list)
                for (int i = 0; i < list.Length && i < Forge.Roles.Length; i++)
                    if (list[i].Length > 0 && files.ContainsKey(list[i])) map[Forge.Roles[i].Reg] = list[i];

            var cursorFiles = files.Keys.Where(k => k.EndsWith(".cur", StringComparison.OrdinalIgnoreCase) || k.EndsWith(".ani", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var f in cursorFiles.Where(f => !map.ContainsValue(f)))
                if (GuessRole(f) is { } reg && !map.ContainsKey(reg)) map[reg] = f;

            var result = new Dictionary<string, CustomCursor>();
            foreach (var (reg, name) in map)
            {
                try { result[reg] = SaveForRole(reg, files[name]()); }
                catch (InvalidDataException) { /* one broken file shouldn't sink the whole pack */ }
            }
            if (result.Count == 0) throw new InvalidDataException("No cursors Squeaker recognises were found in that pack.");
            return result;
        }
        finally { archive?.Dispose(); }
    }

    /// <summary>The 17 file names of the scheme an install.inf registers, in Windows' role order.</summary>
    public static string[]? SchemeFromInf(string text)
    {
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string section = "", schemeLine = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { section = line.Trim('[', ']').ToLowerInvariant(); continue; }
            if (section == "strings" && line.IndexOf('=') is int eq and > 0)
                strings[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"');
            else if (line.Contains(@"Cursors\Schemes", StringComparison.OrdinalIgnoreCase)) schemeLine = line;
        }
        var quoted = Regex.Matches(schemeLine, "\"([^\"]*)\"");
        if (quoted.Count == 0) return null;
        var value = quoted[^1].Groups[1].Value;
        for (int pass = 0; pass < 3; pass++)
            value = Regex.Replace(value, "%([^%]+)%", m => strings.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        var parts = value.Split(',').Select(p => Path.GetFileName(p.Trim().Replace('/', '\\'))).ToArray();
        return parts.Length >= 10 ? parts : null;
    }

    // Most specific words first: "uparrow" must not land on Arrow, "handwriting" not on Hand.
    static readonly (string Reg, string[] Words)[] Keywords =
    [
        ("UpArrow", ["uparrow", "alternate", "alt", "up"]),
        ("NWPen", ["handwriting", "pen", "nwpen"]),
        ("SizeNWSE", ["diagonal1", "dgn1", "nwse", "diagonalresize1", "diag1"]),
        ("SizeNESW", ["diagonal2", "dgn2", "nesw", "diagonalresize2", "diag2"]),
        ("SizeNS", ["vertical", "vert", "ns", "sizens"]),
        ("SizeWE", ["horizontal", "horz", "horiz", "ew", "we", "sizewe"]),
        ("SizeAll", ["move", "sizeall"]),
        ("AppStarting", ["working", "appstarting", "background", "progress"]),
        ("Wait", ["busy", "wait", "loading"]),
        ("Help", ["help"]),
        ("Crosshair", ["precision", "cross", "crosshair"]),
        ("IBeam", ["text", "ibeam", "beam"]),
        ("No", ["unavailable", "unavail", "no", "notallowed"]),
        ("Hand", ["link", "hand"]),
        ("Pin", ["location", "pin"]),
        ("Person", ["person"]),
        ("Arrow", ["normal", "arrow", "pointer", "default", "select"]),
    ];

    public static string? GuessRole(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var tokens = Regex.Split(name, "[^a-z0-9]+").Where(t => t.Length > 0).ToHashSet();
        var squashed = Regex.Replace(name, "[^a-z0-9]", "");
        foreach (var (reg, words) in Keywords)
            if (words.Any(w => tokens.Contains(w) || (w.Length >= 5 && squashed.Contains(w)))) return reg;
        return null;
    }
}
