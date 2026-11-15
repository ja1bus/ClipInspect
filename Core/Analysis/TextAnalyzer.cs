using System.Text;

namespace ClipInspect.Core.Analysis;

/// <summary>テキストの統計（§26.4）。</summary>
public sealed class TextStats
{
    public int Utf16Units { get; init; }
    public int CodePoints { get; init; }
    public int Lines { get; init; }
    public int Crlf { get; init; }
    public int LfOnly { get; init; }
    public int CrOnly { get; init; }
    public int Tabs { get; init; }
    public int OtherControls { get; init; }
    public int Nbsp { get; init; }
    public int ZeroWidth { get; init; }
    public int Bom { get; init; }
    public int LoneSurrogates { get; init; }
    public int ReplacementChars { get; init; }
    public bool EndsWithNewline { get; init; }
    /// <summary>TSV の可能性（推定）。行×列。</summary>
    public (int Rows, int Columns)? TsvShape { get; init; }
}

/// <summary>テキストの終端の読み取り結果。</summary>
public sealed class TerminatedText
{
    public string Text { get; init; } = "";
    /// <summary>終端の NUL の位置（バイト）。なければ null。</summary>
    public long? NulOffset { get; init; }
    /// <summary>終端の NUL の後の確保領域のバイト数（NUL 自体は含めない。§37 Q2）。</summary>
    public long BytesAfterNul { get; init; }
    public int Utf16DataBytes { get; init; }
}

public static class TextAnalyzer
{
    static TextAnalyzer() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>コードページの Encoding（置換文字で復号）。未知のコードページは null。</summary>
    public static Encoding? GetEncoding(int codePage)
    {
        try
        {
            return Encoding.GetEncoding(codePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>UTF-16LE を最初の NUL（2バイト単位）まで読む。奇数の最後のバイトは無視する。</summary>
    public static TerminatedText ReadUtf16(ReadOnlySpan<byte> data)
    {
        var units = data.Length / 2;
        var nul = -1;
        for (var i = 0; i < units; i++)
        {
            if (data[2 * i] == 0 && data[2 * i + 1] == 0) { nul = i; break; }
        }
        var count = nul >= 0 ? nul : units;
        // 不正なサロゲートを置換文字にしないよう、UTF-16 の単位をそのまま文字列にする。
        var text = new string(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(data[..(count * 2)]));
        return new TerminatedText
        {
            Text = text,
            NulOffset = nul >= 0 ? nul * 2L : null,
            BytesAfterNul = nul >= 0 ? data.Length - (nul * 2L + 2) : 0,
            Utf16DataBytes = count * 2,
        };
    }

    /// <summary>1バイト系のテキストを最初の NUL バイトまで読み、指定のコードページで文字にする。</summary>
    public static TerminatedText ReadSingleByte(ReadOnlySpan<byte> data, Encoding encoding)
    {
        var nul = data.IndexOf((byte)0);
        var body = nul >= 0 ? data[..nul] : data;
        return new TerminatedText
        {
            Text = encoding.GetString(body),
            NulOffset = nul >= 0 ? nul : null,
            BytesAfterNul = nul >= 0 ? data.Length - (nul + 1L) : 0,
            Utf16DataBytes = body.Length,
        };
    }

    public static TextStats Analyze(string text)
    {
        int crlf = 0, lf = 0, cr = 0, tabs = 0, other = 0, nbsp = 0, zw = 0, bom = 0, lone = 0, repl = 0, cps = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; cps++; continue; }
                lone++; cps++; continue;
            }
            if (char.IsLowSurrogate(c)) { lone++; cps++; continue; }
            cps++;
            switch (c)
            {
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; cps++; }
                    else cr++;
                    break;
                case '\n': lf++; break;
                case '\t': tabs++; break;
                case ' ': nbsp++; break;
                case '​' or '‌' or '‍' or '⁠': zw++; break;
                case '﻿': bom++; break;
                case '�': repl++; break;
                default:
                    if (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F)) other++;
                    break;
            }
        }
        var endsWithNewline = text.EndsWith('\n') || text.EndsWith('\r');
        var breaks = crlf + lf + cr;
        var lines = text.Length == 0 ? 0 : breaks + (endsWithNewline ? 0 : 1);
        return new TextStats
        {
            Utf16Units = text.Length,
            CodePoints = cps,
            Lines = lines,
            Crlf = crlf,
            LfOnly = lf,
            CrOnly = cr,
            Tabs = tabs,
            OtherControls = other,
            Nbsp = nbsp,
            ZeroWidth = zw,
            Bom = bom,
            LoneSurrogates = lone,
            ReplacementChars = repl,
            EndsWithNewline = endsWithNewline,
            TsvShape = DetectTsv(text),
        };
    }

    /// <summary>
    /// TSV の可能性（推定）。タブを含み、空でない各行のタブの数が同じなら「行×列」。
    /// Excel のセル範囲のコピーは各行のタブの数が揃い、末尾に改行が付く。
    /// </summary>
    public static (int Rows, int Columns)? DetectTsv(string text)
    {
        if (!text.Contains('\t')) return null;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var rows = 0;
        var tabs = -1;
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            var n = line.Count(ch => ch == '\t');
            if (tabs < 0) tabs = n;
            else if (n != tabs) return null;
            rows++;
        }
        if (tabs <= 0) return null;
        return (rows, tabs + 1);
    }

    /// <summary>統計を情報の項目にする。</summary>
    public static void AddItems(FormatAnalysis a, TextStats s)
    {
        a.Add("文字数（UTF-16 単位）", s.Utf16Units.ToString("N0"));
        a.Add("文字数（コードポイント）", s.CodePoints.ToString("N0"));
        a.Add("行数", s.Lines.ToString("N0"));
        a.Add("改行", $"CRLF {s.Crlf:N0} / LFのみ {s.LfOnly:N0} / CRのみ {s.CrOnly:N0}");
        a.Add("末尾の改行", s.EndsWithNewline ? "あり" : "なし");
        a.Add("タブ", s.Tabs.ToString("N0"));
        a.Add("その他の制御文字", s.OtherControls.ToString("N0"));
        if (s.Nbsp + s.ZeroWidth + s.Bom > 0) a.Add("特殊な空白", $"NBSP {s.Nbsp} / ゼロ幅文字 {s.ZeroWidth} / BOM(U+FEFF) {s.Bom}");
        if (s.LoneSurrogates > 0) a.Warnings.Add($"不正なサロゲート（対になっていない）が {s.LoneSurrogates} 個あります。");
        if (s.ReplacementChars > 0) a.Add("置換文字（U+FFFD）", s.ReplacementChars.ToString("N0"));
        if (s.TsvShape is { } t) a.Add("TSV", $"TSV形式の可能性 {t.Rows}行×{t.Columns}列", estimated: true);
    }
}
