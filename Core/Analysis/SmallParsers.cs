using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ClipInspect.Core.Analysis;

/// <summary>CF_HDROP（DROPFILES）の解析結果。</summary>
public sealed class DropFilesInfo
{
    public uint PFiles { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public bool NonClient { get; init; }
    public bool Wide { get; init; }
    public List<string> Paths { get; } = [];
    public string? Error { get; set; }
    public List<string> Warnings { get; } = [];
}

public static class DropFilesParser
{
    public static DropFilesInfo Parse(ReadOnlySpan<byte> d, Encoding ansi)
    {
        if (d.Length < 20)
            return new DropFilesInfo { Error = $"データが {d.Length} バイトしかなく、DROPFILES（20バイト）を読めません。" };
        var info = new DropFilesInfo
        {
            PFiles = BinaryPrimitives.ReadUInt32LittleEndian(d),
            X = BinaryPrimitives.ReadInt32LittleEndian(d[4..]),
            Y = BinaryPrimitives.ReadInt32LittleEndian(d[8..]),
            NonClient = BinaryPrimitives.ReadInt32LittleEndian(d[12..]) != 0,
            Wide = BinaryPrimitives.ReadInt32LittleEndian(d[16..]) != 0,
        };
        if (info.PFiles < 20 || info.PFiles >= d.Length)
        {
            info.Error = $"pFiles（{info.PFiles}）がデータの範囲外です（{d.Length} バイト）。";
            return info;
        }
        var rest = d[(int)info.PFiles..];
        var terminated = false;
        if (info.Wide)
        {
            var units = rest.Length / 2;
            var start = 0;
            for (var i = 0; i < units; i++)
            {
                if (rest[2 * i] != 0 || rest[2 * i + 1] != 0) continue;
                if (i == start) { terminated = true; break; }
                info.Paths.Add(Encoding.Unicode.GetString(rest[(2 * start)..(2 * i)]));
                start = i + 1;
            }
            if (!terminated && start < units) info.Paths.Add(Encoding.Unicode.GetString(rest[(2 * start)..(2 * units)]));
        }
        else
        {
            var start = 0;
            for (var i = 0; i < rest.Length; i++)
            {
                if (rest[i] != 0) continue;
                if (i == start) { terminated = true; break; }
                info.Paths.Add(ansi.GetString(rest[start..i]));
                start = i + 1;
            }
            if (!terminated && start < rest.Length) info.Paths.Add(ansi.GetString(rest[start..]));
        }
        if (!terminated) info.Warnings.Add("ファイル一覧の終端（空の項目）がありません。");
        return info;
    }
}

/// <summary>DWORD の形式（保護指定・CF_LOCALE・DropEffect 等）の解析結果。</summary>
public sealed class DwordInfo
{
    public uint? Value { get; init; }
    /// <summary>形式があることで効く指定か（値は参考）。</summary>
    public bool PresenceMarker { get; init; }
    public string Meaning { get; init; } = "";
}

public static class DwordAnalyzer
{
    public const string ExcludeMonitor = "ExcludeClipboardContentFromMonitorProcessing";
    public const string ViewerIgnore = "Clipboard Viewer Ignore";
    public const string CanIncludeHistory = "CanIncludeInClipboardHistory";
    public const string CanUploadCloud = "CanUploadToCloudClipboard";

    public static readonly string[] PresenceMarkers = [ExcludeMonitor, ViewerIgnore];
    public static readonly string[] ValueMarkers = [CanIncludeHistory, CanUploadCloud];
    public static readonly string[] OtherDwords = ["Preferred DropEffect", "Performed DropEffect", "Paste Succeeded"];

    public static bool IsMarker(string name) =>
        PresenceMarkers.Contains(name, StringComparer.OrdinalIgnoreCase) || ValueMarkers.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static uint? ReadValue(ReadOnlySpan<byte> d) => d.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(d) : null;

    /// <summary>保護指定の形式の解釈（§34 D3）。dataUnreadable は GetClipboardData 等の失敗。</summary>
    public static DwordInfo Marker(string name, ReadOnlySpan<byte> d, bool dataUnreadable)
    {
        var value = dataUnreadable ? null : ReadValue(d);
        if (PresenceMarkers.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            var what = name.Equals(ExcludeMonitor, StringComparison.OrdinalIgnoreCase)
                ? "クリップボードの履歴・監視の対象外"
                : "クリップボードビューアーに無視させる";
            return new DwordInfo { Value = value, PresenceMarker = true, Meaning = $"指定あり（形式があることで効く: {what}）" };
        }
        var subject = name.Equals(CanIncludeHistory, StringComparison.OrdinalIgnoreCase) ? "クリップボードの履歴に含める" : "クラウドクリップボードへ同期する";
        string meaning = value switch
        {
            null => $"値を読めない（PlainClip・ClipCompose は禁止とみなす）: {subject}",
            0 => $"禁止（値 0）: {subject}",
            _ => $"許可（値 {value}）: {subject}",
        };
        return new DwordInfo { Value = value, Meaning = meaning };
    }

    public static DwordInfo DropEffect(ReadOnlySpan<byte> d)
    {
        var v = ReadValue(d);
        if (v is null) return new DwordInfo { Meaning = "値を読めない（4バイト未満）" };
        var parts = new List<string>();
        if ((v & 1) != 0) parts.Add("コピー");
        if ((v & 2) != 0) parts.Add("移動");
        if ((v & 4) != 0) parts.Add("リンク");
        return new DwordInfo { Value = v, Meaning = parts.Count == 0 ? "なし" : string.Join("と", parts) };
    }

    public static DwordInfo Plain(ReadOnlySpan<byte> d)
    {
        var v = ReadValue(d);
        return v is null ? new DwordInfo { Meaning = "値を読めない（4バイト未満）" } : new DwordInfo { Value = v, Meaning = $"値 {v}" };
    }

    public static DwordInfo Locale(ReadOnlySpan<byte> d)
    {
        var v = ReadValue(d);
        if (v is null) return new DwordInfo { Meaning = "値を読めない（4バイト未満）" };
        string name;
        try { name = CultureInfo.GetCultureInfo((int)v.Value).Name; }
        catch (CultureNotFoundException) { name = "不明なロケール"; }
        return new DwordInfo { Value = v, Meaning = $"LCID 0x{v:X4}（{name}）" };
    }
}

/// <summary>16進数の表示（§37 Q15: オフセット8桁・印字できない文字は '.'）。</summary>
public static class HexDump
{
    public static string Format(ReadOnlySpan<byte> data, int limit, out bool truncated)
    {
        truncated = data.Length > limit;
        var d = truncated ? data[..limit] : data;
        var sb = new StringBuilder(d.Length * 4 + 64);
        for (var off = 0; off < d.Length; off += 16)
        {
            var line = d.Slice(off, Math.Min(16, d.Length - off));
            sb.Append(off.ToString("X8")).Append("  ");
            for (var i = 0; i < 16; i++)
            {
                if (i < line.Length) sb.Append(line[i].ToString("X2")).Append(' ');
                else sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(" |");
            foreach (var b in line) sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
            sb.Append('|');
            if (off + 16 < d.Length) sb.Append("\r\n");
        }
        return sb.ToString();
    }
}

/// <summary>制御文字の可視化（§28.1）。</summary>
public static class ControlChars
{
    public static string Visualize(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\t': sb.Append('→'); break;
                case '\r': sb.Append('␍'); break;
                case '\n': sb.Append("␊\n"); break;
                case ' ' or '​' or '‌' or '‍' or '⁠' or '﻿':
                    sb.Append($"[U+{(int)c:X4}]");
                    break;
                default:
                    if (c < 0x20) sb.Append((char)(0x2400 + c));
                    else if (c == 0x7F) sb.Append('␡');
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}

/// <summary>URL の省略（概要レポート用。§37 Q14）。</summary>
public static class UrlMasker
{
    public static string Mask(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var u = url.Trim();
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)) return "（解析できない URL のため省略）";
        if (!string.IsNullOrEmpty(uri.Host) && u.Contains("://", StringComparison.Ordinal))
            return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}/…";
        return $"{uri.Scheme}:…";
    }
}

/// <summary>先頭の署名による画像の種類。</summary>
public static class ImageSignature
{
    public static string? Detect(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return "PNG";
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "JPEG";
        if (d.Length >= 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8') return "GIF";
        if (d.Length >= 2 && d[0] == 'B' && d[1] == 'M') return "BMP";
        if (d.Length >= 4 && ((d[0] == 'I' && d[1] == 'I' && d[2] == 42 && d[3] == 0) || (d[0] == 'M' && d[1] == 'M' && d[2] == 0 && d[3] == 42))) return "TIFF";
        if (d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return "WebP";
        return null;
    }
}
