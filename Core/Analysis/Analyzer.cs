using System.Text;
using ClipInspect.Core.Model;

namespace ClipInspect.Core.Analysis;

/// <summary>取得したデータを形式ごとに解析する（ロックの外で行う。§26.4）。</summary>
public static class Analyzer
{
    /// <summary>解析して f.Analysis に入れる。例外は f.AnalysisError に入れ、他の形式に影響させない。</summary>
    public static void AnalyzeInto(ClipFormat f, CodePageContext cp)
    {
        try
        {
            f.AnalysisError = null;
            f.Analysis = Analyze(f, cp);
        }
        catch (Exception ex)
        {
            f.Analysis = null;
            f.AnalysisError = $"解析でエラーが起きました: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public static FormatAnalysis Analyze(ClipFormat f, CodePageContext cp)
    {
        var a = new FormatAnalysis();
        if (f.Status == FetchStatus.SizeLimited)
            a.Warnings.Add($"サイズ制限により先頭 {f.Data?.Length ?? 0:N0} バイトだけを取得しました（元のサイズ {f.DataSize:N0} バイト）。解析は取得した部分だけです。");

        if (f.Bitmap is { } bmp)
        {
            a.Add("幅×高さ", $"{bmp.Width}×{bmp.Height} ピクセル");
            a.Add("ビット数", $"{bmp.BitsPixel} bpp（プレーン {bmp.Planes}、1行 {bmp.WidthBytes} バイト）");
            a.Add("DIBセクション", bmp.IsDibSection ? $"はい（元のビット数 {bmp.DibSectionBitCount} bpp）" : "いいえ（デバイス依存のビットマップ）");
            if (bmp.Bgra32 is not null) a.Add("画素", "GetDIBits で 32bpp・上から下に変換した値を表示します（元の DDB の値とは別）。");
            if (bmp.PixelsOmittedReason is not null) a.Add("画素", bmp.PixelsOmittedReason);
            if (bmp.PixelError is not null) a.Warnings.Add($"GetDIBits に失敗しました（{bmp.PixelError}）。");
            a.Image = new ImageSummary
            {
                Width = bmp.Width,
                Height = bmp.Height,
                BitDepth = $"{(bmp.IsDibSection ? bmp.DibSectionBitCount : bmp.BitsPixel)} bpp",
                Alpha = "（DDB のため判定しない）",
                Orientation = "（ハンドルのため該当なし）",
            };
            return a;
        }
        if (f.EnhMetafile is { } emf)
        {
            a.Add("範囲（デバイス単位）", $"({emf.BoundsLeft},{emf.BoundsTop})-({emf.BoundsRight},{emf.BoundsBottom})");
            a.Add("レコード数", emf.Records.ToString("N0"));
            a.Add("バイト数", emf.Bytes.ToString("N0"));
            return a;
        }
        if (f.MetafilePict is { } mp)
        {
            a.Add("マッピングモード", mp.MappingMode.ToString());
            a.Add("範囲", $"{mp.XExt}×{mp.YExt}");
            a.Add("メタファイルのバイト数", mp.MetafileBytes.ToString("N0"));
            return a;
        }
        if (f.PaletteEntries is { } pe)
        {
            a.Add("項目数", pe.ToString("N0"));
            return a;
        }

        if (f.Kind == FormatKind.Marker)
        {
            var unreadable = f.Status is not (FetchStatus.Ok or FetchStatus.Empty or FetchStatus.SizeLimited);
            var info = DwordAnalyzer.Marker(f.Name, f.Data ?? [], unreadable);
            a.Dword = info;
            a.Add("形式", "あり");
            a.Add("値", info.Value is { } v ? $"0x{v:X8}" + (info.PresenceMarker ? "（参考）" : "") : "（読めない）");
            a.Add("解釈", info.Meaning + "（Windowsの規約に基づく解釈）");
            return a;
        }

        var data = f.Data;
        if (data is null) return a;

        switch (f.Kind)
        {
            case FormatKind.Locale:
            {
                var info = DwordAnalyzer.Locale(data);
                a.Dword = info;
                a.Add("値", info.Value is { } v ? $"0x{v:X8}" : "（読めない）");
                a.Add("解釈", info.Meaning);
                if (cp.LocaleAnsi is { } la) a.Add("ANSI/OEM コードページ", $"{la} / {cp.LocaleOem}");
                break;
            }
            case FormatKind.Dword:
            {
                var info = f.Name.Contains("DropEffect", StringComparison.OrdinalIgnoreCase) ? DwordAnalyzer.DropEffect(data) : DwordAnalyzer.Plain(data);
                a.Dword = info;
                a.Add("値", info.Value is { } v ? $"0x{v:X8}" : "（読めない）");
                a.Add("解釈", info.Meaning);
                break;
            }
            case FormatKind.Text:
                AnalyzeText(a, f, data, cp);
                break;
            case FormatKind.Html:
            {
                var h = CfHtmlParser.Parse(data);
                a.Html = h;
                a.Add("Version", h.Version ?? "（なし）");
                a.Add("StartHTML", Show(h.StartHtml) + (h.NoContext ? "（文脈なし）" : ""));
                a.Add("EndHTML", Show(h.EndHtml));
                a.Add("StartFragment", Show(h.StartFragment));
                a.Add("EndFragment", Show(h.EndFragment));
                if (h.StartSelection is not null || h.EndSelection is not null)
                    a.Add("StartSelection / EndSelection", $"{Show(h.StartSelection)} / {Show(h.EndSelection)}");
                a.Add("SourceURL", h.SourceUrl ?? "（なし）");
                a.Add("有効データ長", $"{h.ValidLength:N0} バイト（最初の NUL まで）");
                a.Add("NUL の後", h.HasNul ? $"{h.BytesAfterNul:N0} バイト（終端の NUL を除く）" : "終端の NUL なし");
                a.Add("オフセット", h.OffsetsUsable ? "バイト位置として使える" : "使えない");
                a.Warnings.AddRange(h.Warnings);
                a.Text = h.FragmentText ?? h.RawText;
                a.TextLanguage = "html";
                break;
            }
            case FormatKind.Rtf:
            {
                var r = RtfAnalyzer.Analyze(data, cp.SystemAnsi);
                a.Rtf = r;
                a.Add("{\\rtf", r.HasRtfHeader ? $"あり（版 {r.Version?.ToString() ?? "不明"}）" : "なし");
                a.Add("\\ansicpg", r.AnsiCodePage?.ToString() ?? "なし");
                a.Add("括弧の対応", r.BracesBalanced ? "OK" : "対応していない");
                a.Add("8ビットの文字", r.HasEightBit ? "あり" : "なし");
                a.Add("ソースの文字コード", $"コードページ {r.DecodeCodePage}" + (r.DecodeCodePageEstimated ? "（\\ansicpg がないためシステムの ANSI）" : "（\\ansicpg による）"), r.DecodeCodePageEstimated);
                if (!r.HasRtfHeader) a.Warnings.Add("{\\rtf で始まっていません（RTF ではないか、破損しています）。");
                if (!r.BracesBalanced) a.Warnings.Add("括弧 { } の対応が取れていません（破損の可能性）。");
                a.Text = r.Source;
                a.TextLanguage = "rtf";
                break;
            }
            case FormatKind.Image when f.Id is FormatCatalog.CF_DIB or FormatCatalog.CF_DIBV5:
            {
                var dib = DibParser.Parse(data);
                a.Dib = dib;
                DibParser.AddItems(a, dib);
                if (dib.Error is null) a.Image = DibParser.Summary(dib);
                break;
            }
            case FormatKind.Image:
            {
                var sig = ImageSignature.Detect(data);
                a.ImageSignature = sig;
                a.Add("署名による種類", sig ?? "不明");
                if (sig == "PNG")
                {
                    var p = PngParser.Parse(data);
                    a.Png = p;
                    PngParser.AddItems(a, p);
                    a.Image = PngParser.Summary(p);
                }
                else if (FormatCatalog.IsImageName(f.Name) && f.Name.Contains("png", StringComparison.OrdinalIgnoreCase))
                {
                    a.Warnings.Add("形式名は PNG ですが、PNG の署名がありません（破損しているか、別の形式です）。");
                }
                break;
            }
            case FormatKind.FileList:
            {
                var files = DropFilesParser.Parse(data, TextAnalyzer.GetEncoding(cp.Ansi) ?? Encoding.Latin1);
                a.Files = files;
                if (files.Error is not null)
                {
                    a.Warnings.Add($"DROPFILES を解析できません: {files.Error}");
                    break;
                }
                a.Add("pFiles", files.PFiles.ToString());
                a.Add("座標", $"({files.X},{files.Y})");
                a.Add("fNC", files.NonClient ? "1" : "0");
                a.Add("fWide", files.Wide ? "1（UTF-16）" : "0（ANSI）");
                a.Add("ファイル数", files.Paths.Count.ToString());
                a.Warnings.AddRange(files.Warnings);
                a.Text = string.Join("\r\n", files.Paths);
                break;
            }
        }
        return a;
    }

    private static string Show(long? v) => v?.ToString() ?? "（なし）";

    private static void AnalyzeText(FormatAnalysis a, ClipFormat f, byte[] data, CodePageContext cp)
    {
        var enc = FormatCatalog.TextEncodingOf(f.Id, f.Name);
        TerminatedText t;
        switch (enc)
        {
            case "utf16":
                t = TextAnalyzer.ReadUtf16(data);
                a.Add("文字コード", "UTF-16LE（形式の規約）");
                a.Add("終端", t.NulOffset is { } n ? $"NUL（位置 {n:N0} バイト）" : "NUL なし（確保サイズの終わりまで）");
                break;
            case "utf8":
                t = TextAnalyzer.ReadSingleByte(data, Encoding.UTF8);
                a.Add("文字コード", "UTF-8", estimated: true);
                a.Add("終端", t.NulOffset is { } n2 ? $"NUL（位置 {n2:N0} バイト）" : "NUL なし（確保サイズの終わりまで）");
                break;
            default:
            {
                var oem = enc == "oem";
                var page = oem ? cp.Oem : cp.Ansi;
                var e = TextAnalyzer.GetEncoding(page) ?? Encoding.Latin1;
                t = TextAnalyzer.ReadSingleByte(data, e);
                a.Add("文字コード", $"{(oem ? "OEM" : "ANSI")} コードページ {page}（{(oem ? cp.OemSource : cp.AnsiSource)}）", estimated: true);
                a.Add("終端", t.NulOffset is { } n3 ? $"NUL（位置 {n3:N0} バイト）" : "NUL なし（確保サイズの終わりまで）");
                break;
            }
        }
        a.Add("データのバイト数", $"{t.Utf16DataBytes:N0}（終端を除く） / 確保 {data.Length:N0}");
        if (t.NulOffset is not null) a.Add("NUL の後の確保領域", $"{t.BytesAfterNul:N0} バイト（内容は表示しない）");
        var stats = TextAnalyzer.Analyze(t.Text);
        a.TextStats = stats;
        TextAnalyzer.AddItems(a, stats);
        a.Text = t.Text;
    }

    /// <summary>取得済みの画像形式を並べ、寸法の違いを警告する（§26.6）。</summary>
    public static List<string> CompareImages(IEnumerable<ClipFormat> formats)
    {
        var warnings = new List<string>();
        var sized = formats.Where(f => f.Analysis?.Image is { Width: > 0 }).ToList();
        if (sized.Select(f => (f.Analysis!.Image!.Width, f.Analysis.Image.Height)).Distinct().Count() > 1)
        {
            warnings.Add("画像の寸法が形式によって違います: " + string.Join("、", sized.Select(f => $"{f.Name} {f.Analysis!.Image!.Width}×{f.Analysis.Image.Height}")));
        }
        return warnings;
    }
}
