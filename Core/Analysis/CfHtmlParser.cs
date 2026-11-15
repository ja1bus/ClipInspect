using System.Buffers;
using System.Text;

namespace ClipInspect.Core.Analysis;

/// <summary>CF_HTML（"HTML Format"）の解析結果（§26.5）。オフセットはすべてバイト位置。</summary>
public sealed class CfHtmlInfo
{
    public Dictionary<string, string> Header { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Version { get; set; }
    public long? StartHtml { get; set; }
    public long? EndHtml { get; set; }
    public long? StartFragment { get; set; }
    public long? EndFragment { get; set; }
    public long? StartSelection { get; set; }
    public long? EndSelection { get; set; }
    public string? SourceUrl { get; set; }

    /// <summary>ヘッダーの終わり（バイト）。</summary>
    public int HeaderEnd { get; set; }
    /// <summary>有効データの長さ（最初の NUL まで）。</summary>
    public int ValidLength { get; set; }
    public long BytesAfterNul { get; set; }
    public bool HasNul { get; set; }
    public bool NoContext { get; set; }
    public int InvalidUtf8Bytes { get; set; }

    public string HeaderText { get; set; } = "";
    public string? HtmlText { get; set; }
    public string? FragmentText { get; set; }
    /// <summary>オフセットが使えないときに表示する、ヘッダーの後の全体。</summary>
    public string? RawText { get; set; }
    public bool OffsetsUsable { get; set; }

    public List<string> Warnings { get; } = [];
}

public static class CfHtmlParser
{
    private static readonly byte[] StartMarker = "<!--StartFragment-->"u8.ToArray();
    private static readonly byte[] EndMarker = "<!--EndFragment-->"u8.ToArray();
    private const int MaxHeaderBytes = 8192;

    public static CfHtmlInfo Parse(ReadOnlySpan<byte> data)
    {
        var info = new CfHtmlInfo();
        var nul = data.IndexOf((byte)0);
        info.HasNul = nul >= 0;
        info.ValidLength = nul >= 0 ? nul : data.Length;
        info.BytesAfterNul = nul >= 0 ? data.Length - (nul + 1L) : 0;
        var valid = data[..info.ValidLength];
        if (valid.Length == 0)
        {
            info.Warnings.Add("データが空です。");
            info.RawText = "";
            return info;
        }

        ParseHeader(valid, info);
        info.HeaderText = Utf8(valid[..info.HeaderEnd]);
        info.InvalidUtf8Bytes = CountInvalidUtf8(valid);
        if (info.HeaderEnd == 0)
        {
            info.Warnings.Add("ヘッダー（Version・StartHTML 等）がありません。");
            info.RawText = Utf8(valid);
            if (info.InvalidUtf8Bytes > 0) info.Warnings.Add($"不正な UTF-8 が {info.InvalidUtf8Bytes} バイトあります。");
            return info;
        }

        info.Version = Get(info, "Version");
        info.SourceUrl = Get(info, "SourceURL");
        var ok = true;
        info.StartHtml = Number(info, "StartHTML", required: true, ref ok);
        info.EndHtml = Number(info, "EndHTML", required: true, ref ok);
        info.StartFragment = Number(info, "StartFragment", required: true, ref ok);
        info.EndFragment = Number(info, "EndFragment", required: true, ref ok);
        var dummy = true;
        info.StartSelection = Number(info, "StartSelection", required: false, ref dummy);
        info.EndSelection = Number(info, "EndSelection", required: false, ref dummy);
        if (info.Version is null) info.Warnings.Add("必須項目 Version がありません。");

        var len = info.ValidLength;
        info.NoContext = info.StartHtml == -1 && info.EndHtml == -1;

        // 範囲
        foreach (var (key, value, allowMinus) in new[] { ("StartHTML", info.StartHtml, true), ("EndHTML", info.EndHtml, true), ("StartFragment", info.StartFragment, false), ("EndFragment", info.EndFragment, false), ("StartSelection", info.StartSelection, false), ("EndSelection", info.EndSelection, false) })
        {
            if (value is not { } v) continue;
            if (allowMinus && v == -1) continue;
            if (v < 0 || v > len)
            {
                info.Warnings.Add($"{key} の値 {v} が範囲外です（有効データ長 {len} バイト）。");
                if (key is not ("StartSelection" or "EndSelection")) ok = false;
            }
        }

        if (ok)
        {
            long sh = info.StartHtml!.Value, eh = info.EndHtml!.Value, sf = info.StartFragment!.Value, ef = info.EndFragment!.Value;
            // 順序
            if ((sh != -1 && sh > sf) || sf > ef || (eh != -1 && ef > eh))
            {
                info.Warnings.Add($"オフセットの順序が正しくありません（StartHTML ≤ StartFragment ≤ EndFragment ≤ EndHTML を満たさない）。");
                ok = false;
            }
            // ヘッダーの中を指すオフセットは使わない（ヘッダーの SourceURL 等が本文として扱われるのを防ぐ。R3）。
            if (sh != -1 && sh < info.HeaderEnd)
            {
                info.Warnings.Add($"StartHTML（{sh}）がヘッダーの途中を指しています（ヘッダーの終わり {info.HeaderEnd}）。");
                ok = false;
            }
            if (sf < info.HeaderEnd)
            {
                info.Warnings.Add($"StartFragment（{sf}）がヘッダーの途中を指しています（ヘッダーの終わり {info.HeaderEnd}）。");
                ok = false;
            }

            // UTF-8 の文字の途中
            foreach (var (key, v) in new[] { ("StartHTML", sh), ("EndHTML", eh), ("StartFragment", sf), ("EndFragment", ef) })
            {
                if (v > 0 && v < len && (valid[(int)v] & 0xC0) == 0x80)
                    info.Warnings.Add($"{key}（{v}）が UTF-8 の文字の途中を指しています。");
            }

            // マーカーとの一致
            var startMarker = valid.IndexOf(StartMarker);
            var endMarker = valid.IndexOf(EndMarker);
            if (startMarker < 0 || endMarker < 0)
            {
                info.Warnings.Add("<!--StartFragment--> または <!--EndFragment--> のマーカーがありません。");
            }
            else
            {
                var expectedStart = startMarker + StartMarker.Length;
                var expectedEnd = endMarker;
                var charStart = Encoding.UTF8.GetCharCount(valid[..expectedStart]);
                var charEnd = Encoding.UTF8.GetCharCount(valid[..expectedEnd]);
                var looksLikeChars = (sf != expectedStart && sf == charStart) || (ef != expectedEnd && ef == charEnd);
                if (looksLikeChars)
                {
                    info.Warnings.Add($"オフセットが文字数（UTF-16 単位）で計算された可能性があります（推定。StartFragment {sf}・バイト位置 {expectedStart}・文字位置 {charStart}）。");
                }
                else
                {
                    if (sf != expectedStart) info.Warnings.Add($"StartFragment（{sf}）が <!--StartFragment--> の直後（{expectedStart}）と一致しません。");
                    if (ef != expectedEnd) info.Warnings.Add($"EndFragment（{ef}）が <!--EndFragment--> の位置（{expectedEnd}）と一致しません。");
                }
            }

            if (eh != -1 && eh != len)
                info.Warnings.Add($"EndHTML（{eh}）と有効データ長（{len}）の差が {len - eh} バイトあります。");
        }

        if (info.InvalidUtf8Bytes > 0) info.Warnings.Add($"不正な UTF-8 が {info.InvalidUtf8Bytes} バイトあります。");

        info.OffsetsUsable = ok;
        if (ok)
        {
            long sh = info.StartHtml!.Value, eh = info.EndHtml!.Value;
            if (!info.NoContext && sh != -1 && eh != -1) info.HtmlText = Utf8(valid[(int)sh..(int)eh]);
            info.FragmentText = Utf8(valid[(int)info.StartFragment!.Value..(int)info.EndFragment!.Value]);
        }
        else
        {
            info.Warnings.Add("オフセットを使えないため、ヘッダーの後の全体を UTF-8 として表示します。");
            info.RawText = Utf8(valid[info.HeaderEnd..]);
        }
        return info;
    }

    private static string? Get(CfHtmlInfo info, string key) => info.Header.TryGetValue(key, out var v) ? v : null;

    private static long? Number(CfHtmlInfo info, string key, bool required, ref bool ok)
    {
        if (!info.Header.TryGetValue(key, out var text))
        {
            if (required)
            {
                info.Warnings.Add($"必須項目 {key} がありません。");
                ok = false;
            }
            return null;
        }
        if (long.TryParse(text.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
        info.Warnings.Add($"{key} の値「{text}」が数値ではありません。");
        if (required) ok = false;
        return null;
    }

    /// <summary>先頭から "Key:Value" の行を読む。最初の '&lt;' の行か、形に合わない行で止める。</summary>
    private static void ParseHeader(ReadOnlySpan<byte> data, CfHtmlInfo info)
    {
        var pos = 0;
        var limit = Math.Min(data.Length, MaxHeaderBytes);
        while (pos < limit)
        {
            if (data[pos] == (byte)'<') break;
            var end = pos;
            while (end < data.Length && data[end] != (byte)'\r' && data[end] != (byte)'\n') end++;
            var line = data[pos..end];
            var colon = line.IndexOf((byte)':');
            if (colon <= 0 || !IsKey(line[..colon])) break;
            var key = Encoding.ASCII.GetString(line[..colon]);
            info.Header.TryAdd(key, Utf8(line[(colon + 1)..]));
            pos = end;
            if (pos < data.Length && data[pos] == (byte)'\r') pos++;
            if (pos < data.Length && data[pos] == (byte)'\n') pos++;
        }
        info.HeaderEnd = info.Header.Count == 0 ? 0 : pos;
    }

    private static bool IsKey(ReadOnlySpan<byte> key)
    {
        foreach (var b in key)
        {
            if (!((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z'))) return false;
        }
        return true;
    }

    private static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    public static int CountInvalidUtf8(ReadOnlySpan<byte> data)
    {
        var invalid = 0;
        while (!data.IsEmpty)
        {
            var status = System.Text.Rune.DecodeFromUtf8(data, out _, out var consumed);
            if (status != OperationStatus.Done) invalid += consumed;
            data = data[Math.Max(consumed, 1)..];
        }
        return invalid;
    }
}
