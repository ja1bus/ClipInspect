using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using ClipInspect.Core.Analysis;
using ClipInspect.Core.Capture;
using ClipInspect.Core.Model;
using ClipInspect.Core.Native;
using ClipInspect.Core.Reports;
using ClipInspect.Services;

namespace ClipInspect.Views;

/// <summary>メイン画面（仕様書 §28・§34）。取得結果はメモリだけに置き、次の取得まで保持する。</summary>
public partial class MainWindow : Window
{
    private const string OverwriteNote = "調査していたクリップボードの内容は置き換わり、元に戻りません。取得結果は画面に残っています。";

    private readonly Win32ClipboardApi _api = new();
    private readonly ClipboardInspector _inspector;
    private readonly ClipboardWriter _writer = new(new Win32ClipboardWriterApi());
    private readonly ChangeTracker _tracker = new();
    private readonly ObservableCollection<FormatRow> _rows = [];
    private ClipboardOwnerWindow? _owner;
    private CaptureResult? _result;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _lastCaptureFailed;
    private Preview? _preview;
    private int _previewToken;
    private bool _updatingView;
    /// <summary>［詳細レポート］タブの本文が、今の含める形式・取得結果から作った確認済みのものか（R1）。</summary>
    private bool _detailValid;

    public static string AppVersion => Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    public MainWindow()
    {
        InitializeComponent();
        _inspector = new ClipboardInspector(_api);
        FormatList.ItemsSource = _rows;
        Activated += (_, _) => UpdateChangeState();
        Closing += (_, _) => _cts?.Cancel();
        Closed += (_, _) => _owner?.Dispose();
        UpdateButtons();
    }

    // ================= 取得 =================

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "取得中…");
        try
        {
            var r = await Task.Run(() => _inspector.Capture(DateTimeOffset.Now));
            if (r.Outcome == CaptureOutcome.Failed)
            {
                var detail = $"クリップボードを開けませんでした（{r.OpenError}、{r.OpenAttempts} 回試行）。開いているウィンドウ: {DescribeWindow(r.OpenWindow)}";
                if (_result is not null)
                {
                    _lastCaptureFailed = true;
                    OutcomeText.Text = $"今回の取得は失敗（表示は前回 {_result.CapturedAt:HH:mm:ss} の結果）";
                    ShowMessage(detail);
                }
                else
                {
                    OutcomeText.Text = Labels.Of(r.Outcome);
                    CapturedAtText.Text = r.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss");
                    ShowMessage(detail);
                }
                return;
            }

            _lastCaptureFailed = false;
            _result = r;
            _tracker.Reset(r.BaselineSeq);
            _rows.Clear();
            foreach (var f in r.Formats)
            {
                var row = new FormatRow(f);
                row.PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(FormatRow.Include)) InvalidateDetail(); };
                _rows.Add(row);
            }
            InvalidateDetail();
            ShowFormat(null);
            RefreshStatus();
            ShowMessage(r.Formats.Count == 0 ? "クリップボードは空です（形式がありません）。" : "取得しました。形式を選ぶと内容を取得して表示します。OLE・.NET の確認中…");

            var probe = await Task.Run(() => ProbeRunner.Run(FormatProbe.Run, Limits.ProbeTimeout));
            ClipboardInspector.ApplyProbe(r, probe, _api.GetSequenceNumber());
            RefreshStatus();
            ShowMessage(r.Formats.Count == 0 ? "クリップボードは空です（形式がありません）。" : "取得しました。形式を選ぶと内容を取得して表示します。");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void FetchAll_Click(object sender, RoutedEventArgs e) => await FetchAsync(_result?.Formats, all: true);

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>詳細の取得（背景スレッド）。取得できたら true。</summary>
    private async Task<bool> FetchAsync(IEnumerable<ClipFormat>? targets, bool all)
    {
        if (_result is null || targets is null || _busy) return false;
        var r = _result;
        var list = targets.Where(f => f.Status == FetchStatus.NotFetched).ToList();
        if (list.Count == 0) return true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true, all ? $"取得中…（{list.Count} 形式）" : "取得中…");
        try
        {
            var report = await Task.Run(() => _inspector.Fetch(r, list, token));
            if (_result != r) return false;
            switch (report.Outcome)
            {
                case FetchOutcome.OpenFailed:
                    ShowMessage($"クリップボードを開けませんでした（{report.OpenError}）。開いているウィンドウ: {DescribeWindow(report.OpenWindow)}。未取得の形式は未取得のままです。");
                    break;
                case FetchOutcome.ChangedSinceCapture:
                    ShowMessage("取得後にクリップボードが変更されたため、未取得の形式は元のデータを取得できません。");
                    break;
                case FetchOutcome.Canceled:
                    ShowMessage($"中止しました（{report.Fetched} 形式を取得）。残りは未取得です。");
                    break;
                default:
                    _tracker.MoveBaseline(r.BaselineSeq);
                    ShowMessage(all ? $"{report.Fetched} 形式を取得しました。" : $"{list[0].Name} を取得しました。");
                    break;
            }
            foreach (var row in _rows) row.Refresh();
            RefreshStatus();
            if (FormatList.SelectedItem is FormatRow sel) ShowFormat(sel.Format);
            return report.Outcome is FetchOutcome.Done or FetchOutcome.NothingToDo;
        }
        finally
        {
            _cts.Dispose();
            InvalidateDetail();
            _cts = null;
            SetBusy(false);
        }
    }

    // ================= 表示 =================

    private async void FormatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FormatList.SelectedItem is not FormatRow row) return;
        RightTabs.SelectedItem = FormatTab;
        ShowFormat(row.Format);
        if (row.Format.Status == FetchStatus.NotFetched && !_busy)
            await FetchAsync([row.Format], all: false);
    }

    private ClipFormat? Selected => (FormatList.SelectedItem as FormatRow)?.Format;

    private void ShowFormat(ClipFormat? f)
    {
        _preview = null;
        _previewToken++;
        PreviewImage.Source = null;
        if (f is null)
        {
            FormatHeader.Text = _result is null ? "形式を選んでください。" : "左の一覧から形式を選んでください。";
            InfoText.Text = ContentText.Text = HexText.Text = "";
            ImagePanel.Visibility = Visibility.Collapsed;
            ContentText.Visibility = Visibility.Visible;
            SetViewModes([]);
            UpdateButtons();
            return;
        }

        var h = new StringBuilder();
        h.Append($"{f.Index}. {f.Name}　{f.IdText}　区分: {Labels.Of(f.Category)}　種類: {Labels.Of(f.Kind)}\n");
        h.Append($"状態: {Labels.Of(f.Status)}");
        if (f.StatusDetail is not null) h.Append($"（{f.StatusDetail}）");
        if (f.DataSize is { } n) h.Append($"　サイズ: {n:N0} バイト");
        if (f.FetchMilliseconds is { } ms) h.Append($"　GetClipboardData: {ms:F1} ms");
        if (f.Error is not null) h.Append($"\nエラー: {f.Error}");
        if (f.SeqChangedDuringGet) h.Append($"\n取得中に更新番号が変化: {f.SeqBeforeGet} → {f.SeqAfterGet}（描画による可能性。推定）");
        if (f.SynthesisNote is not null) h.Append($"\n{f.SynthesisNote}（推定）");
        FormatHeader.Text = h.ToString();

        var info = new StringBuilder();
        if (f.Analysis is { } a)
        {
            foreach (var item in a.Items) info.Append(item).Append('\n');
            if (a.Warnings.Count > 0)
            {
                info.Append("\n警告:\n");
                foreach (var w in a.Warnings) info.Append("- ").Append(w).Append('\n');
            }
        }
        if (f.AnalysisError is not null) info.Append('\n').Append(f.AnalysisError).Append('\n');
        if (info.Length == 0) info.Append(f.Status == FetchStatus.NotFetched ? "未取得です。" : "表示できる情報はありません。");
        InfoText.Text = info.ToString();

        var data = f.Data ?? f.EnhMetafile?.Data;
        if (data is { Length: > 0 })
        {
            var hex = HexDump.Format(data, Limits.DisplayHexBytes, out var cut);
            HexText.Text = cut ? hex + $"\r\n\r\n（表示の上限により省略: 先頭 {Limits.DisplayHexBytes:N0} バイトを表示。全体 {data.Length:N0} バイト）" : hex;
        }
        else HexText.Text = f.Bitmap is not null ? "（HBITMAP のためバイト列はありません。情報と内容を見てください）" : "（データなし）";

        var modes = new List<string>();
        if (f.Analysis?.Html is { } html)
        {
            if (html.FragmentText is not null) modes.Add("Fragment");
            if (html.HtmlText is not null) modes.Add("HTML 全体");
            modes.Add("ヘッダー");
            if (html.RawText is not null) modes.Add("ヘッダーの後の全体");
        }
        else if (f.Analysis?.Text is null && f.Kind is FormatKind.Binary or FormatKind.Dword && data is { Length: > 0 })
        {
            modes.AddRange(["UTF-8 として表示（推定）", "UTF-16LE として表示（推定）", "ANSI として表示（推定）"]);
        }
        SetViewModes(modes);
        UpdateContent();
        UpdateButtons();

        var isImage = f.Kind == FormatKind.Image && (f.IsFetched || f.Bitmap is not null);
        if (isImage)
        {
            var token = _previewToken;
            ImageNote.Text = "プレビューを作成中…";
            _ = Task.Run(() => ImagePreview.Create(f)).ContinueWith(t =>
            {
                if (token != _previewToken) return;
                _preview = t.IsCompletedSuccessfully ? t.Result : null;
                PreviewImage.Source = _preview?.Image;
                ImageNote.Text = _preview?.Note ?? "プレビューできません。";
                UpdateButtons();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    private void SetViewModes(List<string> modes)
    {
        _updatingView = true;
        ViewMode.ItemsSource = modes;
        ViewMode.SelectedIndex = modes.Count > 0 ? 0 : -1;
        ViewMode.IsEnabled = modes.Count > 0;
        _updatingView = false;
    }

    private void ViewOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingView) return;
        UpdateContent();
    }

    private void DetailTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == DetailTabs) UpdateButtons();
    }

    /// <summary>「内容」タブの全文（表示の上限で切らない）。</summary>
    private string? FullContent(ClipFormat f)
    {
        var a = f.Analysis;
        var mode = ViewMode.SelectedItem as string;
        if (a?.Html is { } h)
        {
            return mode switch
            {
                "HTML 全体" => h.HtmlText,
                "ヘッダー" => h.HeaderText,
                "ヘッダーの後の全体" => h.RawText,
                _ => h.FragmentText ?? h.RawText,
            };
        }
        if (a?.Text is { } t) return t;
        var data = f.Data;
        if (data is { Length: > 0 } && mode is not null)
        {
            if (mode.StartsWith("UTF-8", StringComparison.Ordinal)) return Encoding.UTF8.GetString(data);
            if (mode.StartsWith("UTF-16", StringComparison.Ordinal)) return Encoding.Unicode.GetString(data, 0, data.Length / 2 * 2);
            return (TextAnalyzer.GetEncoding(_result?.CodePages.Ansi ?? 1252) ?? Encoding.Latin1).GetString(data);
        }
        return null;
    }

    private void UpdateContent()
    {
        var f = Selected;
        if (f is null) return;
        var isImage = f.Kind == FormatKind.Image && (f.IsFetched || f.Bitmap is not null);
        ImagePanel.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        ContentText.Visibility = isImage ? Visibility.Collapsed : Visibility.Visible;
        if (isImage) return;

        var text = FullContent(f);
        if (text is null)
        {
            ContentText.Text = f.Status switch
            {
                FetchStatus.NotFetched => "未取得です。",
                FetchStatus.Ok or FetchStatus.Empty or FetchStatus.SizeLimited => f.Kind is FormatKind.Marker or FormatKind.Locale or FormatKind.Dword ? "（値は［情報］タブにあります）" : "（テキストとして表示できる内容はありません。［16進数］を見てください）",
                _ => ErrorText(f),
            };
            return;
        }
        var cut = text.Length > Limits.DisplayTextChars;
        var shown = cut ? text[..Limits.DisplayTextChars] : text;
        if (ShowControls.IsChecked == true) shown = ControlChars.Visualize(shown);
        ContentText.Text = cut ? shown + $"\r\n\r\n（表示の上限により省略: 先頭 {Limits.DisplayTextChars:N0} 文字を表示。全体 {text.Length:N0} 文字。［この内容をコピー］は全体をコピーします）" : shown;
    }

    private static string ErrorText(ClipFormat f) =>
        $"{f.Name}（0x{f.Id:X4}）: {Labels.Of(f.Status)}" + (f.StatusDetail is null ? "" : $"\n理由: {f.StatusDetail}") + (f.Error is null ? "" : $"\n{f.Error}");

    // ================= 状態 =================

    private void RefreshStatus()
    {
        if (_result is { } r)
        {
            CapturedAtText.Text = r.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss");
            if (!_lastCaptureFailed) OutcomeText.Text = Labels.Of(r.Outcome) + (r.Warnings.Count > 0 ? $"（{string.Join(" ", r.Warnings)}）" : "");
            CountText.Text = $"{r.Formats.Count}（未取得 {r.Formats.Count(f => f.Status == FetchStatus.NotFetched)}）";
            SummaryText.Text = BuildReport(detailed: false);
        }
        UpdateChangeState();
        UpdateButtons();
    }

    private void UpdateChangeState()
    {
        if (_result is null) return;
        ChangeText.Text = ChangeTracker.Label(_tracker.Observe(_api.GetSequenceNumber()));
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        if (message is not null) ShowMessage(message);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var has = _result is not null;
        CaptureButton.IsEnabled = !_busy;
        FetchAllButton.IsEnabled = !_busy && has && _result!.Formats.Any(f => f.Status == FetchStatus.NotFetched);
        CancelButton.IsEnabled = _busy && _cts is not null;
        SummaryButton.IsEnabled = DetailButton.IsEnabled = !_busy && has;
        CopyDetailButton.IsEnabled = !_busy && has && _detailValid;
        AboutButton.IsEnabled = !_busy;
        var f = Selected;
        CopyContentButton.IsEnabled = !_busy && f is not null && !(DetailTabs.SelectedItem == ContentTab && ImagePanel.Visibility == Visibility.Visible);
        CopyImageButton.IsEnabled = !_busy && _preview?.Pixels is not null;
    }

    private void ShowMessage(string text) => MessageText.Text = text;

    private static string DescribeWindow(WindowProcessInfo? w) =>
        w is null ? "取得できない（GetOpenClipboardWindow が NULL）"
        : $"0x{w.Hwnd:X} {w.ClassName} " + (w.ExeName is not null ? $"{w.ExeName}（PID {w.ProcessId}）" : $"プロセスを取得できない（PID {w.ProcessId}）");

    // ================= レポート・コピー =================

    private string BuildReport(bool detailed)
    {
        var options = new ReportOptions
        {
            Detailed = detailed,
            IncludedIndexes = _rows.Where(x => x.Include).Select(x => x.Index).ToHashSet(),
            IncludePaths = detailed && OptPaths.IsChecked == true,
            IncludeFullSourceUrl = detailed && OptSourceUrl.IsChecked == true,
            IncludeProcessPath = detailed && OptProcessPath.IsChecked == true,
        };
        return ReportBuilder.Build(_result!, options, AppVersion, _api.GetSequenceNumber(), _tracker.State);
    }

    private enum CopyDecision { Cancel, Proceed, FetchedAll }

    /// <summary>
    /// 未取得の形式があれば確認する（§34 D4）。［はい］で取得が中止・失敗した場合はコピーを止める
    /// （残った形式を取得できなくしない。R2）。
    /// </summary>
    private async Task<CopyDecision> ConfirmUnfetchedAsync()
    {
        if (_result is null) return CopyDecision.Cancel;
        var n = _result.Formats.Count(f => f.Status == FetchStatus.NotFetched);
        if (n == 0) return CopyDecision.Proceed;
        var answer = MessageBox.Show(this,
            $"未取得の形式が {n} 件あります。コピーするとクリップボードの内容が置き換わり、これらは取得できなくなります。\n\n" +
            "［はい］全形式を取得してからコピー\n［いいえ］未取得のままコピー（レポートには未取得と記載）\n［キャンセル］コピーしない",
            "ClipInspect", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.Cancel) return CopyDecision.Cancel;
        if (answer == MessageBoxResult.No) return CopyDecision.Proceed;
        if (await FetchAsync(_result.Formats, all: true)) return CopyDecision.FetchedAll;
        ShowMessage(MessageText.Text + " コピーは行いませんでした（クリップボードはそのままです）。");
        return CopyDecision.Cancel;
    }

    /// <summary>詳細レポートの表示を無効にする（対象・取得結果が変わったとき。R1）。</summary>
    private void InvalidateDetail()
    {
        if (!_detailValid && DetailReportText.Text.Length == 0) return;
        _detailValid = false;
        DetailReportText.Text = "含める形式・取得結果が変わりました。［詳細レポートを確認…］を押して作り直し、内容を確認してからコピーしてください。";
        DetailCount.Text = "";
        UpdateButtons();
    }

    private async Task<bool> WriteAsync(string what, IReadOnlyList<(uint, byte[])> items)
    {
        _owner ??= new ClipboardOwnerWindow();
        _tracker.BeforeWrite(_api.GetSequenceNumber());
        var w = await _writer.WriteAsync(_owner.Handle, items);
        if (w.Status == WriteStatus.Written && w.Sequence is { } seq) _tracker.AfterWrite(seq);
        else _tracker.Observe(_api.GetSequenceNumber());
        ChangeText.Text = ChangeTracker.Label(_tracker.State);
        var ok = w.Status is WriteStatus.Written or WriteStatus.WrittenButReplaced;
        ShowMessage(ok ? $"{what}をコピーしました。{OverwriteNote}" + (w.Status == WriteStatus.WrittenButReplaced ? "（直後に他のアプリがクリップボードを変更しました）" : "") : w.Message);
        return ok;
    }

    private Task<bool> WriteTextAsync(string what, string text) =>
        WriteAsync(what, [(FormatCatalog.CF_UNICODETEXT, ClipboardWriter.UnicodeText(text))]);

    private async void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null || _busy || await ConfirmUnfetchedAsync() == CopyDecision.Cancel) return;
        var report = BuildReport(detailed: false);
        SummaryText.Text = report;
        await WriteTextAsync("概要レポート", report);
    }

    private async void ShowDetail_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null || _busy) return;
        // レポートに含める形式は、コピーの前に取得してメモリに確定する（§11.3）。
        var included = _rows.Where(x => x.Include).Select(x => x.Format).ToList();
        if (included.Any(f => f.Status == FetchStatus.NotFetched)) await FetchAsync(included, all: true);
        RebuildDetail();
        RightTabs.SelectedItem = DetailReportTab;
        if (included.Count == 0) ShowMessage("含める形式にチェックがないため、概要と同じ内容です。左の一覧でチェックを付けてください。");
    }

    private void RebuildDetail()
    {
        if (_result is null) return;
        DetailReportText.Text = BuildReport(detailed: true);
        DetailCount.Text = $"{DetailReportText.Text.Length:N0} 文字";
        _detailValid = true;
        UpdateButtons();
    }

    private void ReportOption_Changed(object sender, RoutedEventArgs e)
    {
        // 表示中のレポートだけを作り直す（作り直した本文はこのタブでそのまま確認できる）。
        if (_result is not null && IsLoaded && _detailValid) RebuildDetail();
    }

    /// <summary>表示して確認した本文だけをコピーする（R1）。</summary>
    private async void CopyDetail_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null || _busy || !_detailValid) return;
        var shown = DetailReportText.Text;
        switch (await ConfirmUnfetchedAsync())
        {
            case CopyDecision.Cancel:
                return;
            case CopyDecision.FetchedAll:
                // 取得で内容が変わったため、作り直して確認してもらう（未確認の本文を送らない）。
                RebuildDetail();
                ShowMessage("全形式を取得してレポートを作り直しました。内容を確認してから、もう一度［コピー］を押してください。");
                return;
        }
        if (!_detailValid || DetailReportText.Text != shown) return;
        await WriteTextAsync("詳細レポート", shown);
    }

    private async void CopyContent_Click(object sender, RoutedEventArgs e)
    {
        var f = Selected;
        if (f is null || _busy) return;
        string? text;
        string what;
        if (DetailTabs.SelectedIndex == 0) { text = InfoText.Text; what = "情報"; }
        else if (DetailTabs.SelectedIndex == 2)
        {
            var data = f.Data ?? f.EnhMetafile?.Data;
            const int max = 1024 * 1024;
            text = data is { Length: > 0 } ? HexDump.Format(data, max, out _) : null;
            what = data is { Length: > max } ? $"16進数（先頭 {max:N0} バイト）" : "16進数";
        }
        else
        {
            text = FullContent(f) ?? (f.IsFetched ? null : ErrorText(f));
            if (text is null && f.Status is FetchStatus.Failed or FetchStatus.SizeFailed or FetchStatus.Unsupported or FetchStatus.Unavailable) text = ErrorText(f);
            what = f.Analysis?.Html is not null ? $"{ViewMode.SelectedItem}" : f.IsFetched ? $"{f.Name} の内容" : "エラーの説明";
        }
        if (string.IsNullOrEmpty(text))
        {
            ShowMessage("コピーできる内容がありません。");
            return;
        }
        if (await ConfirmUnfetchedAsync() == CopyDecision.Cancel) return;
        await WriteTextAsync(what, text);
    }

    private async void CopyImage_Click(object sender, RoutedEventArgs e)
    {
        var px = _preview?.Pixels;
        if (px is null || _busy) return;
        if (await ConfirmUnfetchedAsync() == CopyDecision.Cancel) return;
        SetBusy(true, "画像を変換中…");
        try
        {
            // 変換は背景スレッド、書き込みだけ UI スレッド（§34 D13）。
            var (png, dib) = await Task.Run(() => (ImagePreview.ToPng(px), DibDecoder.ToDib24OnWhite(px)));
            var pngId = new Win32ClipboardWriterApi().RegisterFormat(ClipboardWriter.PngFormatName);
            if (await WriteAsync("画像", [(pngId, png), (FormatCatalog.CF_DIB, dib)]))
                ShowMessage($"変換した画像をコピーしました（元のデータと同一ではありません。PNG と 24bpp の CF_DIB）。{OverwriteNote}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void About_Click(object sender, RoutedEventArgs e) => new VersionWindow { Owner = this }.ShowDialog();
}
