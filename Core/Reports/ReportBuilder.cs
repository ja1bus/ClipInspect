using System.Text;
using ClipInspect.Core.Analysis;
using ClipInspect.Core.Capture;
using ClipInspect.Core.Model;

namespace ClipInspect.Core.Reports;

/// <summary>レポートの種類とオプション（§30・§34 D5）。</summary>
public sealed class ReportOptions
{
    public bool Detailed { get; init; }
    /// <summary>詳細レポートで内容を含める形式（列挙の順の番号）。</summary>
    public IReadOnlySet<int> IncludedIndexes { get; init; } = new HashSet<int>();
    /// <summary>ファイルのパス・URL を含める（パスを含みうる形式の内容）。</summary>
    public bool IncludePaths { get; init; }
    public bool IncludeFullSourceUrl { get; init; }
    public bool IncludeProcessPath { get; init; }
}

/// <summary>AI に渡すための Markdown レポート（§30・§37 Q3〜Q5・Q17）。構造の改行は CRLF。</summary>
public static class ReportBuilder
{
    private const string NL = "\r\n";

    /// <summary>省略の一覧（最大 MaxListItems 件・1件は形式名80文字まで）と注記のために残す文字数（R4）。</summary>
    private const int OmittedReserve = 20_000;
    /// <summary>OLE・.NET の一覧、取得できなかった情報、省略の一覧に出す最大の件数（R4）。</summary>
    private const int MaxListItems = 100;

    public static string Build(CaptureResult r, ReportOptions o, string appVersion, uint currentSeq, ChangeState change)
    {
        var sb = new StringBuilder();
        var omitted = new List<string>();

        // 後ろの節「取得できなかった情報」を先に作り、その長さと省略の一覧の分を残して本文の上限を決める（R4）。
        var notCapturedBlock = new StringBuilder();
        var notCaptured = r.Formats.Where(f => f.Status is not (FetchStatus.Ok or FetchStatus.Empty)).ToList();
        foreach (var f in notCaptured.Take(MaxListItems))
            Line(notCapturedBlock, $"- {f.Index}. {Truncate(f.Name, 80)}: {Labels.Of(f.Status)}" + (f.StatusDetail is null ? "" : $" — {Truncate(f.StatusDetail, 200)}") + (f.Error is null ? "" : $" — {Truncate(f.Error.ToString(), 200)}"));
        if (notCaptured.Count > MaxListItems) Line(notCapturedBlock, $"- ほか {notCaptured.Count - MaxListItems} 形式（形式一覧の状態の列を参照）");
        var bodyLimit = Limits.ReportTotalChars - OmittedReserve - notCapturedBlock.Length;
        Line(sb, o.Detailed ? "# ClipInspect 詳細レポート" : "# ClipInspect 概要レポート");
        Line(sb);

        // ---- 基本情報 ----
        Line(sb, "## 基本情報");
        Line(sb);
        Line(sb, $"- ClipInspect: {appVersion}");
        Line(sb, $"- 取得日時: {r.CapturedAt:yyyy-MM-dd'T'HH:mm:sszzz}");
        Line(sb, $"- Clipboard Sequence Number: 開く前 {r.SeqBeforeOpen} / 開いた直後 {r.SeqAfterOpen} / 閉じる直前 {r.SeqAtClose} / 閉じた後 {r.SeqAfterClose}"
            + (r.SeqAfterProbe is { } sp ? $" / OLE・.NET の確認後 {sp}" : "") + $" / 現在 {currentSeq}（{ChangeTracker.Label(change)}）");
        Line(sb, $"- 取得状態: {Labels.Of(r.Outcome)}");
        if (r.Outcome == CaptureOutcome.Failed)
        {
            Line(sb, $"- 開けなかった理由: {r.OpenError}（{r.OpenAttempts} 回試行）");
            Line(sb, $"- 開いていたウィンドウ: {DescribeWindow(r.OpenWindow, o.IncludeProcessPath)}");
        }
        Line(sb, $"- 形式数: {r.Formats.Count}（CountClipboardFormats: {r.CountFormats}）");
        foreach (var w in r.Warnings) Line(sb, $"- 警告: {w}");
        foreach (var n in r.Notes) Line(sb, $"- 情報: {n}");
        Line(sb);

        // ---- 所有者 ----
        Line(sb, "## クリップボードの所有者（実測）");
        Line(sb);
        if (r.Owner is null)
        {
            Line(sb, "- 所有ウィンドウ: なし（GetClipboardOwner が NULL）");
            Line(sb, "- コピー元プロセス: 取得できない（所有ウィンドウがないため）");
        }
        else
        {
            Line(sb, $"- 所有ウィンドウ: 0x{r.Owner.Hwnd:X}、クラス {r.Owner.ClassName ?? "（取得できない）"}、スレッド {r.Owner.ThreadId}");
            Line(sb, $"- プロセス: {DescribeProcess(r.Owner, o.IncludeProcessPath)}");
        }
        Line(sb, "- 注記: 所有ウィンドウのプロセスは、コピー操作を行ったアプリと一致するとは限りません。形式名からコピー元を推測していません。");
        Line(sb);

        // ---- 形式一覧 ----
        Line(sb, "## 形式一覧");
        Line(sb);
        Line(sb, "| # | ID | 名前 | 区分 | 種類 | サイズ | 状態 | 注記 |");
        Line(sb, "|---|---|---|---|---|---|---|---|");
        for (var fi = 0; fi < r.Formats.Count; fi++)
        {
            var f = r.Formats[fi];
            var notes = new List<string>();
            if (f.SynthesisNote is not null) notes.Add(f.SynthesisNote + Labels.Estimated);
            if (f.Error is not null) notes.Add(f.Error.ToString());
            if (f.StatusDetail is not null && f.Status is not FetchStatus.Ok) notes.Add(f.StatusDetail);
            var row = $"| {f.Index} | {f.IdText} | {Cell(f.Name)} | {Labels.Of(f.Category)} | {Labels.Of(f.Kind)} | {SizeText(f)} | {Labels.Of(f.Status)} | {Cell(string.Join(" / ", notes))} |";
            // 表は本文に使える上限の半分まで（残りを形式ごとの情報に回す）。
            if (sb.Length + row.Length > bodyLimit / 2)
            {
                Line(sb, $"| … | … | 以降の {r.Formats.Count - fi} 形式はレポート全体の上限のため表から省略 | | | | | |");
                omitted.Add($"形式一覧: #{f.Index} 以降の {r.Formats.Count - fi} 形式を、レポート全体の上限（{Limits.ReportTotalChars:N0} 文字）のため表から省略しました。");
                break;
            }
            Line(sb, row);
        }
        if (r.Formats.Count == 0) Line(sb, "| - | - | （形式なし） | - | - | - | - | - |");
        Line(sb);

        // ---- OLE・.NET ----
        Line(sb, "## OLE・.NET から見える形式");
        Line(sb);
        Line(sb, $"- OLE（OleGetClipboard の EnumFormatEtc）: {Labels.Of(r.Probe.OleStatus)}" + (r.Probe.OleError is null ? "" : $"（{r.Probe.OleError}）"));
        foreach (var i in r.Probe.Ole.Take(MaxListItems)) Line(sb, $"  - 0x{i.Id:X4} {Truncate(i.Name, 120)}（{i.Tymed}）{(i.InNativeList ? "" : " ※ネイティブの一覧にない")}");
        if (r.Probe.Ole.Count > MaxListItems) Line(sb, $"  - ほか {r.Probe.Ole.Count - MaxListItems} 件（省略）");
        Line(sb, $"- .NET（WPF の GetFormats）: {Labels.Of(r.Probe.NetStatus)}" + (r.Probe.NetError is null ? "" : $"（{r.Probe.NetError}）"));
        foreach (var i in r.Probe.Net.Take(MaxListItems))
            Line(sb, $"  - {Truncate(i.Name, 120)}（0x{i.Id:X4}）{(i.InNativeDataFormats == false ? " ※変換して取得できる名前（GetFormats(true) だけ）" : "")}{(i.InNativeList ? "" : " ※ネイティブの一覧にない")}");
        if (r.Probe.Net.Count > MaxListItems) Line(sb, $"  - ほか {r.Probe.Net.Count - MaxListItems} 件（省略）");
        Line(sb);

        // ---- 保護指定 ----
        Line(sb, "## 保護指定");
        Line(sb);
        var markers = r.Formats.Where(f => f.Kind == FormatKind.Marker).ToList();
        if (markers.Count == 0) Line(sb, "- 保護指定の形式はありません。");
        foreach (var m in markers)
            Line(sb, $"- {m.Name}: {m.Analysis?.Dword?.Meaning ?? Labels.Of(m.Status)}" + (m.Analysis?.Dword?.Value is { } v ? $"（値 0x{v:X8}）" : ""));
        Line(sb, "- 注記: Windowsの規約に基づく解釈です。OS・貼り付け先のアプリが従うかは保証されません。");
        Line(sb);

        // ---- 画像の比較 ----
        Line(sb, "## 画像の比較");
        Line(sb);
        var images = r.Formats.Where(f => f.Analysis?.Image is not null).ToList();
        var notFetchedImages = r.Formats.Where(f => f.Kind == FormatKind.Image && !f.IsFetched).ToList();
        if (images.Count == 0) Line(sb, "- 取得済みの画像形式はありません。");
        else
        {
            Line(sb, "| 形式 | 幅 | 高さ | ビット深度 | アルファ | 上下方向 |");
            Line(sb, "|---|---|---|---|---|---|");
            foreach (var f in images)
            {
                var i = f.Analysis!.Image!;
                Line(sb, $"| {Cell(f.Name)} | {i.Width} | {i.Height} | {Cell(i.BitDepth)} | {Cell(i.Alpha)} | {Cell(i.Orientation)} |");
            }
            foreach (var w in Analyzer.CompareImages(r.Formats)) Line(sb, $"- 警告: {w}");
        }
        foreach (var f in notFetchedImages) Line(sb, $"- {f.Name}: {Labels.Of(f.Status)}（比較に含めていません）");
        Line(sb);

        // ---- 形式ごとの情報 ----
        Line(sb, "## 形式ごとの情報");
        Line(sb);
        var sectionsOmitted = 0;
        foreach (var f in r.Formats)
        {
            if (sb.Length > bodyLimit) { sectionsOmitted++; continue; }
            var section = new StringBuilder();
            Line(section, $"### {f.Index}. {f.Name}（{f.IdText}）");
            Line(section);
            Line(section, $"- 状態: {Labels.Of(f.Status)}" + (f.StatusDetail is null ? "" : $"（{f.StatusDetail}）"));
            if (f.Error is not null) Line(section, $"- エラー: {f.Error}");
            if (f.DataSize is not null) Line(section, $"- サイズ: {f.DataSize:N0} バイト");
            if (f.FetchMilliseconds is { } ms) Line(section, $"- GetClipboardData の所要時間: {ms:F1} ms");
            if (f.SeqChangedDuringGet) Line(section, $"- 取得中に更新番号が変化: {f.SeqBeforeGet} → {f.SeqAfterGet}（コピー元の描画による可能性{Labels.Estimated}）");
            if (f.SynthesisNote is not null) Line(section, $"- {f.SynthesisNote}{Labels.Estimated}");
            if (f.AnalysisError is not null) Line(section, $"- 解析のエラー: {f.AnalysisError}");
            var hidePaths = FormatCatalog.MayContainPaths(f.Id, f.Name) && !o.IncludePaths;
            if (f.Analysis is { } a)
            {
                foreach (var item in a.Items)
                {
                    if (item.Label == "SourceURL" && !(o.Detailed && o.IncludeFullSourceUrl))
                    {
                        Line(section, $"- SourceURL: {(a.Html?.SourceUrl is { } u ? UrlMasker.Mask(u) + "（省略）" : "（なし）")}");
                        continue;
                    }
                    Line(section, $"- {Inline(item.ToString())}");
                }
                foreach (var w in a.Warnings) Line(section, $"- 警告: {Inline(w)}");
            }

            if (o.Detailed && o.IncludedIndexes.Contains(f.Index))
            {
                if (hidePaths)
                {
                    Line(section, "- 内容: オプション「ファイルのパス・URL を含める」がオフのため省略しました。");
                }
                else AppendContent(section, f, omitted);
            }
            Line(section);

            if (sb.Length + section.Length > bodyLimit)
            {
                if (omitted.Count < MaxListItems) omitted.Add($"{f.Index}. {Truncate(f.Name, 80)}: レポート全体の上限（{Limits.ReportTotalChars:N0} 文字）を超えるため、この形式の節を省略しました。");
                else sectionsOmitted++;
                continue;
            }
            sb.Append(section);
        }

        // ---- 取得できなかった情報・省略 ----
        Line(sb, "## 取得できなかった情報・省略");
        Line(sb);
        var any = notCaptured.Count > 0;
        sb.Append(notCapturedBlock);
        if (r.Owner is { ProcessError: { } pe }) { any = true; Line(sb, $"- コピー元プロセスの情報: 取得できない（{pe}）"); }
        if (r.Owner is null && r.Outcome != CaptureOutcome.Failed) { any = true; Line(sb, "- コピー元プロセスの情報: 所有ウィンドウがないため取得できない"); }
        foreach (var x in omitted) { any = true; Line(sb, $"- {x}"); }
        if (sectionsOmitted > 0) { any = true; Line(sb, $"- ほか {sectionsOmitted} 形式の節を、レポート全体の上限（{Limits.ReportTotalChars:N0} 文字）のため省略しました。"); }
        if (!o.Detailed) { any = true; Line(sb, "- 概要レポートのため、テキスト・HTML・RTF の本文、16進数、ファイルのパス、プロセスのフルパス、SourceURL のパス以降は含めていません。"); }
        if (!any) Line(sb, "- なし");
        Line(sb);

        Line(sb, "## 注記");
        Line(sb);
        Line(sb, $"- 「{Labels.Estimated}」は推定です。それ以外は Windows API で実測した値です。");
        Line(sb, "- 遅延レンダリングかどうかは Windows API で判別できないため、GetClipboardData の所要時間（実測）だけを示しています。");
        Line(sb, "- このレポートをコピーすると、調査していたクリップボードの内容は置き換わります。");
        // 最後の安全策: それでも上限を超える場合は行の区切りで切り、その旨を書く。
        if (sb.Length > Limits.ReportTotalChars)
        {
            const string tail = "\r\n（レポート全体の上限により、以降を省略しました）\r\n";
            var text = sb.ToString(0, Limits.ReportTotalChars - tail.Length);
            var cutAt = text.LastIndexOf(NL, StringComparison.Ordinal);
            return (cutAt > 0 ? text[..cutAt] : text) + tail;
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>1形式の内容。複数のフェンス（HTML 全体と Fragment など）で1形式の上限を共有する（R5）。</summary>
    private static void AppendContent(StringBuilder s, ClipFormat f, List<string> omitted)
    {
        var budget = Limits.ReportFormatChars;
        var a = f.Analysis;
        if (a?.Html is { } h)
        {
            if (h.HtmlText is not null) Fence(s, "HTML 全体（StartHTML〜EndHTML）", h.HtmlText, "html", ref budget);
            if (h.FragmentText is not null) Fence(s, "Fragment（StartFragment〜EndFragment）", h.FragmentText, "html", ref budget);
            if (h.RawText is not null) Fence(s, "ヘッダーの後の全体（オフセットを使えないため）", h.RawText, "html", ref budget);
            return;
        }
        if (a?.Text is { } text)
        {
            Fence(s, "内容", text, a.TextLanguage, ref budget);
            return;
        }
        var data = f.Data ?? f.EnhMetafile?.Data;
        if (data is { Length: > 0 })
        {
            var hex = HexDump.Format(data, Limits.ReportHexBytes, out var truncated);
            Fence(s, "16進数", hex, "hex", ref budget);
            if (truncated) Line(s, $"- 省略: 先頭 {Limits.ReportHexBytes:N0} バイトだけを出力しました（全体 {data.Length:N0} バイト。レポートの16進数の上限）。");
            return;
        }
        Line(s, "- 内容: 出力できる内容がありません（画像はメタ情報だけを出力します）。");
    }

    private static void Fence(StringBuilder s, string title, string content, string lang, ref int budget)
    {
        if (budget <= 0)
        {
            Line(s, $"- {title}: レポートの1形式の上限（{Limits.ReportFormatChars:N0} 文字）に達したため、{content.Length:N0} 文字すべてを省略しました。");
            return;
        }
        var body = content;
        var cut = 0;
        if (body.Length > budget)
        {
            // サロゲートペアの途中で切らない。
            var len = char.IsHighSurrogate(body[budget - 1]) ? budget - 1 : budget;
            body = body[..len];
            cut = content.Length - len;
        }
        budget -= body.Length;
        var fence = new string('`', Math.Max(3, LongestBacktickRun(body) + 1));
        Line(s, $"- {title}:");
        Line(s);
        Line(s, fence + lang);
        s.Append(body);
        if (!body.EndsWith('\n') && !body.EndsWith('\r')) s.Append(NL);
        Line(s, fence);
        Line(s);
        if (cut > 0) Line(s, $"- 省略: 先頭 {body.Length:N0} 文字だけを出力し、残り {cut:N0} 文字を省略しました（レポートの1形式の上限 {Limits.ReportFormatChars:N0} 文字）。");
    }

    public static int LongestBacktickRun(string text)
    {
        int max = 0, run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            if (run > max) max = run;
        }
        return max;
    }

    private static string SizeText(ClipFormat f) =>
        f.DataSize is { } n ? $"{n:N0} B" : f.Bitmap is { } b ? $"{b.Width}×{b.Height}" : "-";

    private static string DescribeProcess(WindowProcessInfo w, bool fullPath)
    {
        if (w.ExeName is null) return $"取得できない（PID {w.ProcessId}" + (w.ProcessError is null ? "）" : $"、{w.ProcessError}）");
        return fullPath && w.ExePath is not null ? $"{w.ExePath} (PID {w.ProcessId})" : $"{w.ExeName} (PID {w.ProcessId})";
    }

    private static string DescribeWindow(WindowProcessInfo? w, bool fullPath) =>
        w is null ? "取得できない（GetOpenClipboardWindow が NULL）" : $"0x{w.Hwnd:X}、クラス {w.ClassName ?? "不明"}、プロセス {DescribeProcess(w, fullPath)}";

    /// <summary>表のセル: 縦棒をエスケープし、改行を空白にする。</summary>
    public static string Cell(string text) => text.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

    private static string Inline(string text) => text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

    private static void Line(StringBuilder sb, string text = "") => sb.Append(text).Append(NL);
}
