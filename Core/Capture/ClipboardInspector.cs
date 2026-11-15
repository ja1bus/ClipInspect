using System.Diagnostics;
using ClipInspect.Core.Analysis;
using ClipInspect.Core.Model;
using ClipInspect.Core.Native;

namespace ClipInspect.Core.Capture;

/// <summary>詳細取得の結果。</summary>
public enum FetchOutcome { Done, NothingToDo, OpenFailed, ChangedSinceCapture, Canceled }

public sealed class FetchReport
{
    public FetchOutcome Outcome { get; init; }
    public Win32Error? OpenError { get; init; }
    public WindowProcessInfo? OpenWindow { get; init; }
    public int Fetched { get; init; }
}

/// <summary>
/// クリップボードの取得（列挙）と詳細の取得（§26.1・§26.2・§34 D1）。
/// すべてのメソッドは同じスレッドで Open〜Close を行う。UI スレッドから呼ばない。
/// </summary>
public sealed class ClipboardInspector(IClipboardApi api)
{
    /// <summary>［取得］: 形式の一覧と軽い情報だけを取る（保護指定と CF_LOCALE 以外のデータは読まない）。</summary>
    public CaptureResult Capture(DateTimeOffset now)
    {
        var r = new CaptureResult { CapturedAt = now };
        r.SeqBeforeOpen = api.GetSequenceNumber();
        if (!OpenWithRetry(out var attempts, out var openError))
        {
            r.OpenAttempts = attempts;
            r.Outcome = CaptureOutcome.Failed;
            r.OpenError = new Win32Error(openError, api.ErrorMessage(openError));
            r.OpenWindow = SafeWindowInfo(api.GetOpenWindow());
            return r;
        }
        r.OpenAttempts = attempts;
        nint owner;
        try
        {
            r.SeqAfterOpen = api.GetSequenceNumber();
            owner = api.GetOwner();
            r.CountFormats = api.CountFormats();
            uint prev = 0;
            while (true)
            {
                var id = api.EnumFormats(prev, out var err);
                if (id == 0)
                {
                    if (err != 0) r.EnumError = new Win32Error(err, api.ErrorMessage(err));
                    break;
                }
                if (r.Formats.Count >= Limits.EnumFormats)
                {
                    r.EnumTruncated = true;
                    break;
                }
                var registered = id >= 0xC000 ? api.GetFormatName(id) : null;
                var (name, unavailable) = FormatCatalog.NameOf(id, registered);
                r.Formats.Add(new ClipFormat
                {
                    Index = r.Formats.Count + 1,
                    Id = id,
                    Name = name,
                    NameUnavailable = unavailable,
                    Category = FormatCatalog.CategoryOf(id),
                    Kind = FormatCatalog.KindOf(id, name),
                    Handle = FormatCatalog.HandleOf(id),
                });
                prev = id;
            }
            // 保護指定と CF_LOCALE は小さく、判断に必要なため取得時に読む（§26.1 手順3）。
            foreach (var f in r.Formats.Where(f => f.Kind is FormatKind.Marker or FormatKind.Locale))
                FetchOne(f);
            r.SeqAtClose = api.GetSequenceNumber();
        }
        finally
        {
            api.Close();
        }
        r.SeqAfterClose = api.GetSequenceNumber();
        r.BaselineSeq = r.SeqAtClose;

        FormatCatalog.MarkSynthesized(r.Formats);
        if (owner != 0) r.Owner = SafeWindowInfo(owner);

        var sys = api.GetSystemCodePages();
        var localeFormat = r.Formats.FirstOrDefault(f => f.Id == FormatCatalog.CF_LOCALE && f.Data is { Length: >= 4 });
        uint? lcid = localeFormat is null ? null : BitConverter.ToUInt32(localeFormat.Data!, 0);
        var lcp = lcid is { } l ? api.GetLocaleCodePages(l) : null;
        r.CodePages = new CodePageContext { SystemAnsi = sys.Ansi, SystemOem = sys.Oem, LocaleLcid = lcid, LocaleAnsi = lcp?.Ansi, LocaleOem = lcp?.Oem };
        foreach (var f in r.Formats.Where(f => f.Status != FetchStatus.NotFetched)) Analyzer.AnalyzeInto(f, r.CodePages);

        if (r.SeqBeforeOpen != r.SeqAfterOpen)
            r.Notes.Add($"［取得］を押してから開くまでに更新番号が変わりました（{r.SeqBeforeOpen} → {r.SeqAfterOpen}）。一覧は開いた後の内容です。");
        if (r.SeqAfterOpen != r.SeqAtClose)
            r.Notes.Add($"開いている間に更新番号が変わりました（{r.SeqAfterOpen} → {r.SeqAtClose}）。保護指定・CF_LOCALE の読み取りによる変換・描画の可能性があります（推定）。");
        if (r.EnumTruncated) r.Warnings.Add($"形式が {Limits.EnumFormats} を超えたため、列挙を打ち切りました。");
        if (r.EnumError is not null) r.Warnings.Add($"形式の列挙がエラーで終わりました（{r.EnumError}）。");
        r.Outcome = r.Warnings.Count > 0 ? CaptureOutcome.Warning : CaptureOutcome.Ok;
        return r;
    }

    /// <summary>OLE・.NET の確認の結果を反映し、時点の違い・失敗を警告にする（§34 D1・D2）。</summary>
    public static void ApplyProbe(CaptureResult r, ProbeResult probe, uint seqAfterProbe)
    {
        r.Probe = probe;
        r.SeqAfterProbe = seqAfterProbe;
        var ids = r.Formats.Select(f => f.Id).ToHashSet();
        foreach (var i in probe.Ole.Concat(probe.Net)) i.InNativeList = ids.Contains(i.Id);
        if (seqAfterProbe != r.SeqAtClose)
            r.Warnings.Add($"OLE・.NET の確認の間にクリップボードが変更されました（{r.SeqAtClose} → {seqAfterProbe}）。OLE・.NET の一覧は別の時点のものです。");
        if (probe.OleStatus is ProbeStatus.Failed or ProbeStatus.Timeout)
            r.Warnings.Add($"OLE の形式を{Labels.Of(probe.OleStatus)}" + (probe.OleError is null ? "。" : $"（{probe.OleError}）。"));
        if (probe.NetStatus is ProbeStatus.Failed or ProbeStatus.Timeout)
            r.Warnings.Add($".NET の形式を{Labels.Of(probe.NetStatus)}" + (probe.NetError is null ? "。" : $"（{probe.NetError}）。"));
        if (r.Outcome != CaptureOutcome.Failed) r.Outcome = r.Warnings.Count > 0 ? CaptureOutcome.Warning : CaptureOutcome.Ok;
    }

    /// <summary>
    /// 詳細の取得。targets のうち「未取得」の形式を、1回の Open〜Close で取得する。
    /// 開いた直後の番号が基準と違えば、未取得の形式をすべて「取得不可」にして何も読まない。
    /// </summary>
    public FetchReport Fetch(CaptureResult r, IEnumerable<ClipFormat> targets, CancellationToken cancel = default)
    {
        var list = targets.Where(f => f.Status == FetchStatus.NotFetched).ToList();
        if (list.Count == 0) return new FetchReport { Outcome = FetchOutcome.NothingToDo };
        if (r.ChangedSinceCapture)
        {
            MarkUnavailable(r);
            return new FetchReport { Outcome = FetchOutcome.ChangedSinceCapture };
        }
        if (!OpenWithRetry(out _, out var openError))
        {
            return new FetchReport
            {
                Outcome = FetchOutcome.OpenFailed,
                OpenError = new Win32Error(openError, api.ErrorMessage(openError)),
                OpenWindow = SafeWindowInfo(api.GetOpenWindow()),
            };
        }
        var fetched = new List<ClipFormat>();
        var canceled = false;
        try
        {
            var seqOpen = api.GetSequenceNumber();
            if (seqOpen != r.BaselineSeq)
            {
                r.ChangedSinceCapture = true;
                MarkUnavailable(r);
                return new FetchReport { Outcome = FetchOutcome.ChangedSinceCapture };
            }
            foreach (var f in list)
            {
                if (cancel.IsCancellationRequested) { canceled = true; break; }
                FetchOne(f);
                fetched.Add(f);
            }
            r.BaselineSeq = api.GetSequenceNumber();
        }
        finally
        {
            api.Close();
        }
        foreach (var f in fetched)
        {
            Analyzer.AnalyzeInto(f, r.CodePages);
            // 全体を取得できたテキストで、終端の NUL を除いた本文が空なら「空」とする（R7。失敗・一部取得は上書きしない）。
            if (f.Status == FetchStatus.Ok && f.Kind == FormatKind.Text && f.Analysis?.Text is { Length: 0 })
            {
                f.Status = FetchStatus.Empty;
                f.StatusDetail = "終端の NUL だけで、本文は空文字列です。";
            }
        }
        return new FetchReport { Outcome = canceled ? FetchOutcome.Canceled : FetchOutcome.Done, Fetched = fetched.Count };
    }

    private static void MarkUnavailable(CaptureResult r)
    {
        foreach (var f in r.Formats.Where(f => f.Status == FetchStatus.NotFetched))
        {
            f.Status = FetchStatus.Unavailable;
            f.StatusDetail = "取得後にクリップボードが変更されたため、元のデータを取得できません。";
        }
    }

    /// <summary>1つの形式のデータを写す（クリップボードを開いている間に呼ぶ）。失敗はその形式だけに記録する。</summary>
    private void FetchOne(ClipFormat f)
    {
        if (f.Handle == HandleKind.None)
        {
            f.Status = FetchStatus.Unsupported;
            f.StatusDetail = "所有者が描画する形式で、データがありません（GetClipboardData を呼びません）。";
            return;
        }
        if (f.Handle == HandleKind.Unknown)
        {
            f.Status = FetchStatus.Unsupported;
            f.StatusDetail = "ハンドルの種類が決まっていない形式のため、内容を読みません（GetClipboardData を呼びません）。";
            return;
        }
        try
        {
            var sw = Stopwatch.StartNew();
            f.SeqBeforeGet = api.GetSequenceNumber();
            var h = api.GetData(f.Id, out var err);
            f.SeqAfterGet = api.GetSequenceNumber();
            f.FetchMilliseconds = sw.Elapsed.TotalMilliseconds;
            if (h == 0)
            {
                f.Status = FetchStatus.Failed;
                f.Error = new Win32Error(err, api.ErrorMessage(err));
                f.StatusDetail = "GetClipboardData が NULL を返しました。";
                return;
            }
            switch (f.Handle)
            {
                case HandleKind.Global:
                {
                    var g = api.ReadGlobal(h, FormatCatalog.FetchLimitOf(f.Id, f.Name), Limits.PartialBytes);
                    f.Status = g.Status;
                    f.DataSize = g.Size;
                    f.Data = g.Data;
                    f.Error = g.Error;
                    if (g.Status == FetchStatus.SizeLimited)
                        f.StatusDetail = $"サイズ制限（{FormatCatalog.FetchLimitOf(f.Id, f.Name) / Limits.MiB} MiB）を超えたため、先頭 {g.Data?.Length:N0} バイトだけを取得しました。";
                    else if (g.Status == FetchStatus.SizeFailed)
                        f.StatusDetail = "GlobalSize でデータのサイズを取得できませんでした。";
                    break;
                }
                case HandleKind.Bitmap:
                    f.Bitmap = api.ReadBitmap(h, Limits.BitmapPixelBytes);
                    f.Status = FetchStatus.Ok;
                    break;
                case HandleKind.Palette:
                    f.PaletteEntries = api.ReadPaletteEntries(h);
                    f.Status = FetchStatus.Ok;
                    break;
                case HandleKind.EnhMetafile:
                    f.EnhMetafile = api.ReadEnhMetafile(h, Limits.FetchBytes);
                    f.DataSize = f.EnhMetafile.Bytes;
                    f.Data = f.EnhMetafile.Data;
                    f.Status = FetchStatus.Ok;
                    break;
                case HandleKind.MetafilePict:
                    f.MetafilePict = api.ReadMetafilePict(h);
                    f.Status = FetchStatus.Ok;
                    break;
            }
        }
        catch (Exception ex)
        {
            f.Status = FetchStatus.Failed;
            f.StatusDetail = $"取得中にエラーが起きました: {ex.Message}";
            if (ex is System.ComponentModel.Win32Exception w) f.Error = new Win32Error(w.NativeErrorCode, w.Message);
        }
    }

    private bool OpenWithRetry(out int attempts, out int error)
    {
        error = 0;
        for (attempts = 1; attempts <= Limits.OpenRetryCount; attempts++)
        {
            if (api.Open(out error)) return true;
            if (attempts < Limits.OpenRetryCount) api.Sleep(Limits.OpenRetryDelayMs);
        }
        attempts = Limits.OpenRetryCount;
        return false;
    }

    private WindowProcessInfo? SafeWindowInfo(nint hwnd)
    {
        if (hwnd == 0) return null;
        try { return api.GetWindowProcess(hwnd); }
        catch (Exception ex) { return new WindowProcessInfo { Hwnd = hwnd, ProcessError = new Win32Error(0, ex.Message) }; }
    }
}
