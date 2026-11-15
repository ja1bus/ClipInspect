using ClipInspect.Core.Model;

namespace ClipInspect.Core.Analysis;

/// <summary>形式IDと名前から、名前・区分・種類・ハンドルの種類・取得上限を決める（§26.1・§26.3・§26.4）。</summary>
public static class FormatCatalog
{
    public const uint CF_TEXT = 1, CF_BITMAP = 2, CF_METAFILEPICT = 3, CF_SYLK = 4, CF_DIF = 5, CF_TIFF = 6, CF_OEMTEXT = 7,
        CF_DIB = 8, CF_PALETTE = 9, CF_PENDATA = 10, CF_RIFF = 11, CF_WAVE = 12, CF_UNICODETEXT = 13, CF_ENHMETAFILE = 14,
        CF_HDROP = 15, CF_LOCALE = 16, CF_DIBV5 = 17,
        CF_OWNERDISPLAY = 0x80, CF_DSPTEXT = 0x81, CF_DSPBITMAP = 0x82, CF_DSPMETAFILEPICT = 0x83, CF_DSPENHMETAFILE = 0x8E,
        CF_PRIVATEFIRST = 0x200, CF_PRIVATELAST = 0x2FF, CF_GDIOBJFIRST = 0x300, CF_GDIOBJLAST = 0x3FF;

    private static readonly Dictionary<uint, string> StandardNames = new()
    {
        [CF_TEXT] = "CF_TEXT", [CF_BITMAP] = "CF_BITMAP", [CF_METAFILEPICT] = "CF_METAFILEPICT", [CF_SYLK] = "CF_SYLK",
        [CF_DIF] = "CF_DIF", [CF_TIFF] = "CF_TIFF", [CF_OEMTEXT] = "CF_OEMTEXT", [CF_DIB] = "CF_DIB", [CF_PALETTE] = "CF_PALETTE",
        [CF_PENDATA] = "CF_PENDATA", [CF_RIFF] = "CF_RIFF", [CF_WAVE] = "CF_WAVE", [CF_UNICODETEXT] = "CF_UNICODETEXT",
        [CF_ENHMETAFILE] = "CF_ENHMETAFILE", [CF_HDROP] = "CF_HDROP", [CF_LOCALE] = "CF_LOCALE", [CF_DIBV5] = "CF_DIBV5",
        [CF_OWNERDISPLAY] = "CF_OWNERDISPLAY", [CF_DSPTEXT] = "CF_DSPTEXT", [CF_DSPBITMAP] = "CF_DSPBITMAP",
        [CF_DSPMETAFILEPICT] = "CF_DSPMETAFILEPICT", [CF_DSPENHMETAFILE] = "CF_DSPENHMETAFILE",
    };

    /// <summary>パスや URL を含みうる形式（§34 D5）。</summary>
    public static readonly string[] PathFormatNames =
        ["FileName", "FileNameW", "UniformResourceLocator", "UniformResourceLocatorW", "Shell IDList Array", "FileGroupDescriptor", "FileGroupDescriptorW"];

    public static bool IsStandardRange(uint id) => id < 0xC000;

    /// <summary>名前と、名前を取れなかったか。registeredName は GetClipboardFormatName の結果。</summary>
    public static (string Name, bool Unavailable) NameOf(uint id, string? registeredName)
    {
        if (id >= 0xC000) return registeredName is { Length: > 0 } ? (registeredName, false) : ($"名前なし（0x{id:X4}）", true);
        if (StandardNames.TryGetValue(id, out var n)) return (n, false);
        if (id is >= CF_PRIVATEFIRST and <= CF_PRIVATELAST) return ($"CF_PRIVATEFIRST+{id - CF_PRIVATEFIRST}", false);
        if (id is >= CF_GDIOBJFIRST and <= CF_GDIOBJLAST) return ($"CF_GDIOBJFIRST+{id - CF_GDIOBJFIRST}", false);
        return ($"名前なし（0x{id:X4}）", true);
    }

    public static FormatCategory CategoryOf(uint id)
    {
        if (id >= 0xC000) return FormatCategory.Registered;
        if (StandardNames.ContainsKey(id)) return FormatCategory.Standard;
        if (id is >= CF_PRIVATEFIRST and <= CF_PRIVATELAST) return FormatCategory.Private;
        if (id is >= CF_GDIOBJFIRST and <= CF_GDIOBJLAST) return FormatCategory.GdiObject;
        return FormatCategory.Unknown;
    }

    public static HandleKind HandleOf(uint id)
    {
        if (id is CF_BITMAP or CF_DSPBITMAP) return HandleKind.Bitmap;
        if (id is CF_PALETTE) return HandleKind.Palette;
        if (id is CF_ENHMETAFILE or CF_DSPENHMETAFILE) return HandleKind.EnhMetafile;
        if (id is CF_METAFILEPICT or CF_DSPMETAFILEPICT) return HandleKind.MetafilePict;
        if (id is CF_OWNERDISPLAY) return HandleKind.None;
        if (id is >= CF_PRIVATEFIRST and <= CF_GDIOBJLAST) return HandleKind.Unknown;
        if (id < 0xC000 && !StandardNames.ContainsKey(id)) return HandleKind.Unknown;
        return HandleKind.Global;
    }

    public static FormatKind KindOf(uint id, string name)
    {
        switch (id)
        {
            case CF_TEXT or CF_OEMTEXT or CF_UNICODETEXT or CF_DSPTEXT: return FormatKind.Text;
            case CF_BITMAP or CF_DIB or CF_DIBV5 or CF_DSPBITMAP or CF_TIFF: return FormatKind.Image;
            case CF_METAFILEPICT or CF_ENHMETAFILE or CF_DSPMETAFILEPICT or CF_DSPENHMETAFILE: return FormatKind.Metafile;
            case CF_PALETTE: return FormatKind.Palette;
            case CF_HDROP: return FormatKind.FileList;
            case CF_LOCALE: return FormatKind.Locale;
            case CF_OWNERDISPLAY: return FormatKind.NoData;
        }
        if (id < 0xC000) return FormatKind.Binary;
        if (name.Equals("HTML Format", StringComparison.OrdinalIgnoreCase)) return FormatKind.Html;
        if (name is "Rich Text Format" or "Rich Text Format Without Objects" or "RTF As Text") return FormatKind.Rtf;
        if (DwordAnalyzer.IsMarker(name)) return FormatKind.Marker;
        if (DwordAnalyzer.OtherDwords.Contains(name, StringComparer.OrdinalIgnoreCase)) return FormatKind.Dword;
        if (IsImageName(name)) return FormatKind.Image;
        if (TextEncodingOf(id, name) is not null) return FormatKind.Text;
        return FormatKind.Binary;
    }

    public static bool IsImageName(string name) =>
        name.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || name.ToUpperInvariant() is "PNG" or "JFIF" or "GIF" or "JPEG" or "JPG" or "TIFF" or "BMP" or "WEBP";

    /// <summary>テキストの符号化（"utf16"・"ansi"・"oem"・"utf8"）。テキストでなければ null。</summary>
    public static string? TextEncodingOf(uint id, string name)
    {
        if (id == CF_UNICODETEXT) return "utf16";
        if (id is CF_TEXT or CF_DSPTEXT) return "ansi";
        if (id == CF_OEMTEXT) return "oem";
        if (id < 0xC000) return null;
        if (name is "FileNameW" or "UniformResourceLocatorW") return "utf16";
        if (name is "FileName" or "UniformResourceLocator" or "Csv") return "ansi";
        if (name.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return "utf8";
        return null;
    }

    public static long FetchLimitOf(uint id, string name) =>
        id is CF_DIB or CF_DIBV5 || (id >= 0xC000 && IsImageName(name)) ? Limits.ImageFetchBytes : Limits.FetchBytes;

    public static bool MayContainPaths(uint id, string name) =>
        id == CF_HDROP || PathFormatNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Windows が変換して加える形式と、その元になる形式（§27.1）。</summary>
    private static readonly Dictionary<uint, uint[]> SynthesisSources = new()
    {
        [CF_TEXT] = [CF_UNICODETEXT, CF_OEMTEXT],
        [CF_OEMTEXT] = [CF_UNICODETEXT, CF_TEXT],
        [CF_UNICODETEXT] = [CF_TEXT, CF_OEMTEXT],
        [CF_BITMAP] = [CF_DIB, CF_DIBV5],
        [CF_DIB] = [CF_BITMAP, CF_DIBV5],
        [CF_DIBV5] = [CF_BITMAP, CF_DIB],
        [CF_PALETTE] = [CF_DIB, CF_DIBV5],
        [CF_METAFILEPICT] = [CF_ENHMETAFILE],
        [CF_ENHMETAFILE] = [CF_METAFILEPICT],
    };

    /// <summary>列挙の順に従い、変換形式・CF_LOCALE の自動追加の可能性を付ける（推定）。</summary>
    public static void MarkSynthesized(IReadOnlyList<ClipFormat> formats)
    {
        var seen = new HashSet<uint>();
        foreach (var f in formats)
        {
            if (SynthesisSources.TryGetValue(f.Id, out var sources) && sources.Any(seen.Contains))
            {
                var src = string.Join("・", sources.Where(seen.Contains).Select(s => NameOf(s, null).Name));
                f.SynthesisNote = $"Windowsの変換形式の可能性（{src} から）";
            }
            else if (f.Id == CF_LOCALE && (seen.Contains(CF_TEXT) || seen.Contains(CF_OEMTEXT) || seen.Contains(CF_UNICODETEXT)))
            {
                f.SynthesisNote = "CF_LOCALE の自動追加の可能性";
            }
            seen.Add(f.Id);
        }
    }
}
