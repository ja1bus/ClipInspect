using ClipInspect.Core.Capture;
using ClipInspect.Core.Model;
using ClipInspect.Tests;

namespace ClipInspect.Tests;

/// <summary>取得（列挙）と詳細の取得（試験表 §2・§3）。</summary>
public class CaptureTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 20, 0, 0, TimeSpan.FromHours(9));
    private static readonly byte[] Text = [0x41, 0, 0, 0];

    private static FakeClipboardApi Api(params FakeFormat[] formats)
    {
        var api = new FakeClipboardApi();
        api.Formats.AddRange(formats);
        return api;
    }

    [Fact] // T-05
    public void T05_NamesAndCategories()
    {
        var api = Api(new() { Id = 0x0001 }, new() { Id = 0x000D }, new() { Id = 0x0080 }, new() { Id = 0x0082 }, new() { Id = 0x0200 }, new() { Id = 0x0205 },
            new() { Id = 0x0300 }, new() { Id = 0x03FF }, new() { Id = 0xC123, Name = "ClipInspect.Test" }, new() { Id = 0xC124 }, new() { Id = 0x0400 });
        var r = new ClipboardInspector(api).Capture(Now);
        Assert.Equal(["CF_TEXT", "CF_UNICODETEXT", "CF_OWNERDISPLAY", "CF_DSPBITMAP", "CF_PRIVATEFIRST+0", "CF_PRIVATEFIRST+5", "CF_GDIOBJFIRST+0", "CF_GDIOBJFIRST+255", "ClipInspect.Test", "名前なし（0xC124）", "名前なし（0x0400）"],
            r.Formats.Select(f => f.Name));
        Assert.Equal([FormatCategory.Standard, FormatCategory.Standard, FormatCategory.Standard, FormatCategory.Standard, FormatCategory.Private, FormatCategory.Private,
            FormatCategory.GdiObject, FormatCategory.GdiObject, FormatCategory.Registered, FormatCategory.Registered, FormatCategory.Unknown], r.Formats.Select(f => f.Category));
        Assert.True(r.Formats[9].NameUnavailable);
    }

    [Fact] // T-06
    public void T06_OrderKeptAndDataNotRead()
    {
        var api = Api(new() { Id = 0xC0F1, Name = "HTML Format", Data = Text }, new() { Id = 0x000D, Data = Text }, new() { Id = 0x0001, Data = Text }, new() { Id = 0x0010, Data = [0x11, 4, 0, 0] }, new() { Id = 0x0007, Data = Text });
        var r = new ClipboardInspector(api).Capture(Now);
        Assert.Equal([0xC0F1u, 0x000D, 0x0001, 0x0010, 0x0007], r.Formats.Select(f => f.Id));
        Assert.Equal([1, 2, 3, 4, 5], r.Formats.Select(f => f.Index));
        Assert.Equal(5, r.CountFormats);
        Assert.All(r.Formats.Where(f => f.Id != 0x0010), f => Assert.Equal(FetchStatus.NotFetched, f.Status));
        Assert.Equal([0x0010u], api.GetDataCalls);
    }

    [Fact] // T-07
    public void T07_EnumLimitsAndErrors()
    {
        var a = Api(Enumerable.Range(0, 1000).Select(i => new FakeFormat { Id = (uint)(0xC000 + i), Name = $"F{i}" }).ToArray());
        var ra = new ClipboardInspector(a).Capture(Now);
        Assert.Equal(1000, ra.Formats.Count);
        Assert.False(ra.EnumTruncated);
        Assert.Equal(CaptureOutcome.Ok, ra.Outcome);

        var b = Api(Enumerable.Range(0, 1001).Select(i => new FakeFormat { Id = (uint)(0xC000 + i), Name = $"F{i}" }).ToArray());
        var rb = new ClipboardInspector(b).Capture(Now);
        Assert.Equal(1000, rb.Formats.Count);
        Assert.True(rb.EnumTruncated);
        Assert.Equal(CaptureOutcome.Warning, rb.Outcome);

        var c = Api(new() { Id = 1 }, new() { Id = 13 }, new() { Id = 7 }, new() { Id = 16 });
        c.EnumErrorAfter = 3;
        c.EnumError = 5;
        var rc = new ClipboardInspector(c).Capture(Now);
        Assert.Equal(3, rc.Formats.Count);
        Assert.Equal(CaptureOutcome.Warning, rc.Outcome);
        Assert.Equal(5, rc.EnumError!.Code);
        Assert.Contains(rc.Warnings, w => w.Contains("エラー 5") && w.Contains("説明5"));
    }

    [Fact] // T-08
    public void T08_OpenRetry()
    {
        var a = Api(new FakeFormat { Id = 13 });
        a.OpenSucceeds = _ => false;
        a.OpenWindow = 0x99;
        a.WindowInfo = new WindowProcessInfo { Hwnd = 0x99, ProcessId = 42, ExeName = "busy.exe" };
        var ra = new ClipboardInspector(a).Capture(Now);
        Assert.Equal(10, a.OpenCalls);
        Assert.Equal(CaptureOutcome.Failed, ra.Outcome);
        Assert.Equal(5, ra.OpenError!.Code);
        Assert.Equal("説明5", ra.OpenError.Message);
        Assert.Equal("busy.exe", ra.OpenWindow!.ExeName);
        Assert.Empty(ra.Formats);

        var b = Api(new FakeFormat { Id = 13 });
        b.OpenSucceeds = n => n >= 3;
        Assert.Equal(CaptureOutcome.Ok, new ClipboardInspector(b).Capture(Now).Outcome);
        Assert.Equal(3, b.OpenCalls);

        var c = Api(new FakeFormat { Id = 13 });
        c.OpenSucceeds = n => n >= 10;
        Assert.Equal(CaptureOutcome.Ok, new ClipboardInspector(c).Capture(Now).Outcome);
        Assert.Equal(10, c.OpenCalls);
    }

    private static CaptureResult CaptureWithSeqs(params uint[] seqs)
    {
        var api = Api(new FakeFormat { Id = 13 });
        // Capture は 開く前・開いた直後・閉じる直前・閉じた後 の順に番号を読む。
        foreach (var s in seqs.Take(4)) api.Seqs.Enqueue(s);
        var r = new ClipboardInspector(api).Capture(Now);
        ClipboardInspector.ApplyProbe(r, new ProbeResult { OleStatus = ProbeStatus.Ok, NetStatus = ProbeStatus.Ok }, seqs[4]);
        return r;
    }

    [Fact] // T-09
    public void T09_SequenceRules()
    {
        Assert.Equal(CaptureOutcome.Ok, CaptureWithSeqs(100, 100, 100, 100, 100).Outcome);
        var b = CaptureWithSeqs(100, 101, 101, 101, 101);
        Assert.Equal(CaptureOutcome.Ok, b.Outcome);
        Assert.Contains(b.Notes, n => n.Contains("100 → 101"));
        var c = CaptureWithSeqs(100, 100, 102, 102, 102);
        Assert.Equal(CaptureOutcome.Ok, c.Outcome);
        Assert.Equal(102u, c.BaselineSeq);
        var d = CaptureWithSeqs(101, 101, 101, 101, 102);
        Assert.Equal(CaptureOutcome.Warning, d.Outcome);
        Assert.Contains(d.Warnings, w => w.Contains("別の時点"));
    }

    [Fact] // T-10
    public void T10_SynthesisNotes()
    {
        var a = new ClipboardInspector(Api(new() { Id = 13 }, new() { Id = 1 }, new() { Id = 7 }, new() { Id = 16, Data = [9, 4, 0, 0] })).Capture(Now);
        Assert.Null(a.Formats[0].SynthesisNote);
        Assert.Contains("変換形式の可能性", a.Formats[1].SynthesisNote);
        Assert.Contains("変換形式の可能性", a.Formats[2].SynthesisNote);
        Assert.Contains("CF_LOCALE の自動追加の可能性", a.Formats[3].SynthesisNote);

        var b = new ClipboardInspector(Api(new() { Id = 1 }, new() { Id = 13 })).Capture(Now);
        Assert.Null(b.Formats[0].SynthesisNote);
        Assert.NotNull(b.Formats[1].SynthesisNote);

        var c = new ClipboardInspector(Api(new() { Id = 8 }, new() { Id = 2 }, new() { Id = 17 })).Capture(Now);
        Assert.Null(c.Formats[0].SynthesisNote);
        Assert.NotNull(c.Formats[1].SynthesisNote);
        Assert.NotNull(c.Formats[2].SynthesisNote);
    }

    [Fact] // T-11
    public void T11_OwnerInfo()
    {
        var a = Api(new FakeFormat { Id = 13 });
        a.Owner = 0x1234;
        a.WindowInfo = new WindowProcessInfo { Hwnd = 0x1234, ClassName = "CLIPBRDWNDCLASS", ProcessId = 1234, ThreadId = 7, ExeName = "copy.exe", ExePath = @"C:\Apps\Secret\copy.exe" };
        var ra = new ClipboardInspector(a).Capture(Now);
        Assert.Equal("CLIPBRDWNDCLASS", ra.Owner!.ClassName);
        Assert.Equal(@"C:\Apps\Secret\copy.exe", ra.Owner.ExePath);

        var rb = new ClipboardInspector(Api(new FakeFormat { Id = 13 })).Capture(Now);
        Assert.Null(rb.Owner);

        var c = Api(new FakeFormat { Id = 13 });
        c.Owner = 0x55;
        c.WindowInfo = new WindowProcessInfo { Hwnd = 0x55, ProcessId = 9, ProcessError = new Win32Error(5, "アクセス拒否") };
        var rc = new ClipboardInspector(c).Capture(Now);
        Assert.Equal(5, rc.Owner!.ProcessError!.Code);
    }

    [Theory] // T-12
    [InlineData("CanIncludeInClipboardHistory", new byte[] { 0, 0, 0, 0 }, null, "禁止")]
    [InlineData("CanIncludeInClipboardHistory", new byte[] { 1, 0, 0, 0 }, null, "許可")]
    [InlineData("CanIncludeInClipboardHistory", new byte[] { 1, 0 }, null, "値を読めない")]
    [InlineData("CanIncludeInClipboardHistory", null, 1418, "値を読めない")]
    [InlineData("CanUploadToCloudClipboard", new byte[] { 0, 0, 0, 0 }, null, "禁止")]
    [InlineData("CanUploadToCloudClipboard", new byte[] { 1, 0, 0, 0 }, null, "許可")]
    [InlineData("CanUploadToCloudClipboard", new byte[] { 1, 0 }, null, "値を読めない")]
    [InlineData("ExcludeClipboardContentFromMonitorProcessing", new byte[0], null, "指定あり")]
    [InlineData("ExcludeClipboardContentFromMonitorProcessing", new byte[] { 1, 0, 0, 0 }, null, "指定あり")]
    [InlineData("Clipboard Viewer Ignore", new byte[] { 0 }, null, "指定あり")]
    public void T12_ProtectionMarkers(string name, byte[]? data, int? error, string expected)
    {
        var api = Api(new FakeFormat { Id = 0xC200, Name = name, Data = data, GetDataError = error });
        var r = new ClipboardInspector(api).Capture(Now);
        var f = r.Formats[0];
        Assert.Equal(Core.Model.FormatKind.Marker, f.Kind);
        Assert.Contains(expected, f.Analysis!.Dword!.Meaning);
        Assert.Contains(f.Analysis.Items, i => i.Label == "形式" && i.Value == "あり");
        Assert.Contains(f.Analysis.Items, i => i.Value.Contains("Windowsの規約に基づく解釈"));
    }

    [Fact] // T-12 (h)
    public void T12_NoMarkers()
    {
        var r = new ClipboardInspector(Api(new FakeFormat { Id = 13 })).Capture(Now);
        Assert.DoesNotContain(r.Formats, f => f.Kind == Core.Model.FormatKind.Marker);
    }

    [Fact] // T-13
    public void T13_CaptureReadsOnlyMarkersAndLocale()
    {
        var api = Api(new() { Id = 0x000D, Data = Text }, new() { Id = 0xC0F1, Name = "HTML Format", Data = Text },
            new() { Id = 0xC200, Name = "CanIncludeInClipboardHistory", Data = [0, 0, 0, 0] }, new() { Id = 0x0010, Data = [0x11, 4, 0, 0] });
        var r = new ClipboardInspector(api).Capture(Now);
        Assert.Equal([0xC200u, 0x0010u], api.GetDataCalls);
        Assert.Equal(FetchStatus.NotFetched, r.Formats[0].Status);
        Assert.Equal(FetchStatus.NotFetched, r.Formats[1].Status);
    }

    [Fact] // T-14
    public void T14_FetchOnlySelected_ThenAll()
    {
        var api = Api(new() { Id = 0x000D, Data = Text }, new() { Id = 0xC0F1, Name = "HTML Format", Data = Text }, new() { Id = 0x0008, Data = new byte[40] }, new() { Id = 0xC300, Name = "X", Data = Text });
        var ins = new ClipboardInspector(api);
        var r = ins.Capture(Now);
        api.GetDataCalls.Clear();
        ins.Fetch(r, [r.Formats[1]]);
        Assert.Equal([0xC0F1u], api.GetDataCalls);
        Assert.Equal(FetchStatus.Ok, r.Formats[1].Status);
        Assert.NotNull(r.Formats[1].FetchMilliseconds);
        Assert.Equal(FetchStatus.NotFetched, r.Formats[0].Status);

        var r2 = ins.Capture(Now);
        var sessions = api.OpenSessions;
        ins.Fetch(r2, r2.Formats);
        Assert.Equal(sessions + 1, api.OpenSessions);
        Assert.All(r2.Formats, f => Assert.Equal(FetchStatus.Ok, f.Status));
    }

    [Fact] // T-15
    public void T15_UnsupportedHandles()
    {
        var api = Api(new() { Id = 0x0080 }, new() { Id = 0x0200 }, new() { Id = 0x0300 }, new() { Id = 0x000D, Data = Text });
        var ins = new ClipboardInspector(api);
        var r = ins.Capture(Now);
        ins.Fetch(r, r.Formats);
        Assert.Equal([0x000Du], api.GetDataCalls);
        Assert.Equal(FetchStatus.Unsupported, r.Formats[0].Status);
        Assert.Contains("所有者が描画", r.Formats[0].StatusDetail);
        Assert.Contains("ハンドルの種類が決まっていない", r.Formats[1].StatusDetail);
        Assert.Equal(FetchStatus.Unsupported, r.Formats[2].Status);
        Assert.Equal(FetchStatus.Ok, r.Formats[3].Status);
    }

    [Fact] // T-16
    public void T16_ChangedAfterCapture()
    {
        var api = Api(new() { Id = 0x000D, Data = Text }, new() { Id = 0xC0F1, Name = "HTML Format", Data = Text }, new() { Id = 0xC300, Name = "X", Data = Text });
        var ins = new ClipboardInspector(api);
        var r = ins.Capture(Now); // 番号は 100 のまま
        ins.Fetch(r, [r.Formats[0]]);
        Assert.Equal(FetchStatus.Ok, r.Formats[0].Status);
        api.GetDataCalls.Clear();
        api.Seq = 105;
        var rep = ins.Fetch(r, [r.Formats[1]]);
        Assert.Equal(FetchOutcome.ChangedSinceCapture, rep.Outcome);
        Assert.Empty(api.GetDataCalls);
        Assert.Equal(FetchStatus.Unavailable, r.Formats[1].Status);
        Assert.Equal(FetchStatus.Unavailable, r.Formats[2].Status);
        Assert.Equal(FetchStatus.Ok, r.Formats[0].Status);
        api.Seq = 100;
        ins.Fetch(r, [r.Formats[2]]);
        Assert.Empty(api.GetDataCalls);
    }

    [Fact] // T-17
    public void T17_SeqChangeDuringGet()
    {
        var api = Api(new() { Id = 0xC300, Name = "X", Data = Text, BumpSeqOnGet = true }, new() { Id = 0x000D, Data = Text });
        var ins = new ClipboardInspector(api);
        var r = ins.Capture(Now);
        ins.Fetch(r, [r.Formats[0]]);
        Assert.Equal(FetchStatus.Ok, r.Formats[0].Status);
        Assert.True(r.Formats[0].SeqChangedDuringGet);
        Assert.Equal(101u, r.BaselineSeq);
        ins.Fetch(r, [r.Formats[1]]);
        Assert.Equal(FetchStatus.Ok, r.Formats[1].Status);
    }

    [Fact] // T-18
    public void T18_OneFailureDoesNotAffectOthers()
    {
        var api = Api(new() { Id = 0x000D, Data = Text }, new() { Id = 0xC300, Name = "X", GetDataError = 1418 }, new() { Id = 0xC301, Name = "Y", ThrowOnRead = true }, new() { Id = 0xC0F1, Name = "HTML Format", Data = Text });
        var ins = new ClipboardInspector(api);
        var r = ins.Capture(Now);
        ins.Fetch(r, r.Formats);
        Assert.Equal(FetchStatus.Ok, r.Formats[0].Status);
        Assert.Equal(FetchStatus.Failed, r.Formats[1].Status);
        Assert.Equal(1418, r.Formats[1].Error!.Code);
        Assert.Equal(FetchStatus.Failed, r.Formats[2].Status);
        Assert.Contains("偽の例外", r.Formats[2].StatusDetail);
        Assert.Equal(FetchStatus.Ok, r.Formats[3].Status);
    }

    [Fact] // T-18 (c) 解析の例外
    public void T18_AnalysisExceptionIsolated()
    {
        // CF_TEXT はコードページを使うため、cp が null だと解析が例外になる。例外を外へ出さずに記録する。
        var f = new ClipFormat { Id = 1, Name = "CF_TEXT", Kind = Core.Model.FormatKind.Text, Status = FetchStatus.Ok, Data = Text };
        Core.Analysis.Analyzer.AnalyzeInto(f, null!);
        Assert.Null(f.Analysis);
        Assert.Contains("解析でエラー", f.AnalysisError);
        var g = new ClipFormat { Id = 13, Name = "CF_UNICODETEXT", Kind = Core.Model.FormatKind.Text, Status = FetchStatus.Ok, Data = Text };
        Core.Analysis.Analyzer.AnalyzeInto(g, new CodePageContext());
        Assert.NotNull(g.Analysis);
    }

    [Fact] // T-19
    public void T19_SizeRules()
    {
        var api = Api(new() { Id = 0xC300, Name = "A", Size = 0, SizeError = 0 }, new() { Id = 0xC301, Name = "B", Size = 0, SizeError = 6 },
            new() { Id = 0xC302, Name = "C", Size = 64 * Limits.MiB }, new() { Id = 0xC303, Name = "D", Size = 64 * Limits.MiB + 1 });
        var ins = new ClipboardInspector(api);
        var r = ins.Capture(Now);
        ins.Fetch(r, r.Formats);
        Assert.Equal(FetchStatus.Empty, r.Formats[0].Status);
        Assert.Equal(FetchStatus.SizeFailed, r.Formats[1].Status);
        Assert.Equal(6, r.Formats[1].Error!.Code);
        Assert.Equal(FetchStatus.Ok, r.Formats[2].Status);
        Assert.Equal(FetchStatus.SizeLimited, r.Formats[3].Status);
        Assert.Equal(67_108_865, r.Formats[3].DataSize);
        Assert.Equal((false, 67_108_864), CopyPlanGlobal(64 * Limits.MiB, Limits.FetchBytes));
        Assert.Equal((true, 65_536), CopyPlanGlobal(64 * Limits.MiB + 1, Limits.FetchBytes));
    }

    private static (bool, int) CopyPlanGlobal(long size, long limit) => Core.Native.CopyPlan.Global(size, limit, Limits.PartialBytes);

    [Fact] // T-20
    public void T20_ImageLimits()
    {
        var api = Api(new() { Id = 0x0008, Size = 256 * Limits.MiB }, new() { Id = 0xC0AA, Name = "PNG", Size = 256 * Limits.MiB + 1 });
        var ins = new ClipboardInspector(api);
        var r = ins.Capture(Now);
        ins.Fetch(r, r.Formats);
        Assert.All(api.ReadLimits, l => Assert.Equal(Limits.ImageFetchBytes, l));
        Assert.Equal(FetchStatus.Ok, r.Formats[0].Status);
        Assert.Equal(FetchStatus.SizeLimited, r.Formats[1].Status);
        Assert.True(Core.Native.CopyPlan.BitmapPixels(8192, 8192, Limits.BitmapPixelBytes));
        Assert.False(Core.Native.CopyPlan.BitmapPixels(8193, 8192, Limits.BitmapPixelBytes));
    }

    [Fact] // Q11: 詳細取得で開けない場合は未取得のまま
    public void FetchOpenFailureKeepsNotFetched()
    {
        var api = Api(new FakeFormat { Id = 13, Data = Text });
        var ins = new ClipboardInspector(api);
        var r = ins.Capture(Now);
        api.OpenSucceeds = _ => false;
        var rep = ins.Fetch(r, r.Formats);
        Assert.Equal(FetchOutcome.OpenFailed, rep.Outcome);
        Assert.Equal(FetchStatus.NotFetched, r.Formats[0].Status);
    }

    [Fact] // D11: 中止
    public void FetchCanBeCanceledBetweenFormats()
    {
        var api = Api(new() { Id = 13, Data = Text }, new() { Id = 0xC300, Name = "X", Data = Text });
        var ins = new ClipboardInspector(api);
        var r = ins.Capture(Now);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var rep = ins.Fetch(r, r.Formats, cts.Token);
        Assert.Equal(FetchOutcome.Canceled, rep.Outcome);
        Assert.All(r.Formats, f => Assert.Equal(FetchStatus.NotFetched, f.Status));
    }

    [Fact] // Q9: OLE・.NET の確認の打ち切り
    public void ProbeTimeout()
    {
        var r = ProbeRunner.Run(() => { Thread.Sleep(5000); return new ProbeResult(); }, TimeSpan.FromMilliseconds(200));
        Assert.Equal(ProbeStatus.Timeout, r.OleStatus);
        var cap = new ClipboardInspector(Api(new FakeFormat { Id = 13 })).Capture(Now);
        ClipboardInspector.ApplyProbe(cap, r, cap.SeqAtClose);
        Assert.Equal(CaptureOutcome.Warning, cap.Outcome);
        Assert.Contains(cap.Warnings, w => w.Contains("タイムアウト"));

        var failed = ProbeRunner.Run(() => throw new InvalidOperationException("x"), TimeSpan.FromSeconds(2));
        Assert.Equal(ProbeStatus.Failed, failed.NetStatus);
    }

    [Fact] // §34 D9
    public void ChangeTrackerKeepsOtherChange()
    {
        var t = new ChangeTracker();
        t.Reset(100);
        Assert.Equal(ChangeState.Unchanged, t.Observe(100));
        t.BeforeWrite(105);
        t.AfterWrite(108);
        Assert.Equal(ChangeState.ChangedByOtherThenClipInspect, t.State);

        var u = new ChangeTracker();
        u.Reset(100);
        u.BeforeWrite(100);
        u.AfterWrite(103);
        Assert.Equal(ChangeState.ChangedByClipInspect, u.Observe(103));
        Assert.Equal(ChangeState.ChangedByOther, u.Observe(110));
    }
}
