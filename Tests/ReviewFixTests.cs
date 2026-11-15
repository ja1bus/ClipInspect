using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using ClipInspect.Core.Analysis;
using ClipInspect.Core.Capture;
using ClipInspect.Core.Model;
using ClipInspect.Core.Reports;

namespace ClipInspect.Tests;

/// <summary>Codex レビュー（docs/code-review-20261115.md）の R3〜R7 の回帰確認。R1・R2 は画面の経路のため手動の回帰確認。</summary>
public class ReviewFixTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 23, 0, 0, TimeSpan.FromHours(9));

    private static string Build(CaptureResult r, bool detailed, IEnumerable<int>? include = null, bool url = false) =>
        ReportBuilder.Build(r, new ReportOptions { Detailed = detailed, IncludedIndexes = (include ?? []).ToHashSet(), IncludeFullSourceUrl = url },
            "1.0.0", r.SeqAfterClose, ChangeState.Unchanged);

    private static CaptureResult CaptureAll(params FakeFormat[] formats)
    {
        var api = new FakeClipboardApi();
        api.Formats.AddRange(formats);
        var ins = new ClipboardInspector(api);
        var r = ins.Capture(Now);
        ins.Fetch(r, r.Formats);
        return r;
    }

    // ---- R3 ----
    [Fact]
    public void R3_StartHtmlInsideHeaderIsUnusableAndDoesNotLeakSourceUrl()
    {
        var bytes = AnalysisTests.CfHtml("<b>x</b>", "https://example.com/private?token=SECRET",
            mutate: t => Regex.Replace(t, @"StartHTML:\d+", "StartHTML:0000000000"));
        var h = CfHtmlParser.Parse(bytes);
        Assert.False(h.OffsetsUsable);
        Assert.Contains(h.Warnings, w => w.Contains("ヘッダーの途中"));
        Assert.NotNull(h.RawText);
        Assert.DoesNotContain("token=SECRET", h.RawText);
        Assert.StartsWith("<html>", h.RawText);

        var r = CaptureAll(new FakeFormat { Id = 0xC0F1, Name = "HTML Format", Data = bytes });
        var off = Build(r, true, [1]);
        Assert.DoesNotContain("token=SECRET", off);
        Assert.DoesNotContain("/private", off);
        Assert.Contains("token=SECRET", Build(r, true, [1], url: true));
    }

    [Fact]
    public void R3_StartFragmentInsideHeaderIsUnusable()
    {
        var bytes = AnalysisTests.CfHtml("<b>x</b>", "https://example.com/p?token=S",
            mutate: t => Regex.Replace(Regex.Replace(t, @"StartHTML:\d+", "StartHTML:-000000001"), @"StartFragment:\d+", "StartFragment:0000000005"));
        Assert.False(CfHtmlParser.Parse(bytes).OffsetsUsable);
    }

    // ---- R4 ----
    [Fact]
    public void R4_ManyLongFormatsStayWithinTotalLimit()
    {
        var formats = Enumerable.Range(0, 1000).Select(i => new FakeFormat { Id = (uint)(0xC000 + i), Name = $"F{i:D4}" + new string('a', 245) }).ToArray();
        var api = new FakeClipboardApi();
        api.Formats.AddRange(formats);
        var r = new ClipboardInspector(api).Capture(Now);
        r.Probe.Ole.AddRange(Enumerable.Range(0, 1000).Select(i => new ProbeItem { Id = (uint)(0xC000 + i), Name = new string('o', 250), Tymed = "TYMED_HGLOBAL" }));
        r.Probe.Net.AddRange(Enumerable.Range(0, 1000).Select(i => new ProbeItem { Id = (uint)(0xC000 + i), Name = new string('n', 250) }));
        var summary = Build(r, false);
        Assert.True(summary.Length <= Limits.ReportTotalChars, summary.Length.ToString());
        Assert.Contains("表から省略", summary);
        Assert.Contains("## 注記", summary);
        var detail = Build(r, true, Enumerable.Range(1, 1000));
        Assert.True(detail.Length <= Limits.ReportTotalChars, detail.Length.ToString());
    }

    [Fact]
    public void R4_ManyErrorsStayWithinTotalLimit()
    {
        var r = CaptureAll(Enumerable.Range(0, 1000).Select(i => new FakeFormat { Id = (uint)(0xC000 + i), Name = $"E{i:D4}" + new string('e', 245), GetDataError = 1418 }).ToArray());
        var text = Build(r, false);
        Assert.True(text.Length <= Limits.ReportTotalChars, text.Length.ToString());
        Assert.Contains("ほか 900 形式", text);
    }

    // ---- R5 ----
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void R5_HtmlFullAndFragmentShareFormatLimit(int delta)
    {
        // HTML 全体（Fragment＋前後の固定の文字列）と Fragment の合計が、1形式の上限 100,000 文字の前後になるようにする。
        var wrapper = CfHtmlParser.Parse(AnalysisTests.CfHtml("x")).HtmlText!.Length - 1;
        var target = Limits.ReportFormatChars + delta;
        var n = (target - wrapper) / 2;
        var frag = new string('x', n) + new string('y', target - wrapper - 2 * n); // 奇数の差は Fragment に1文字足さず、合計を合わせる
        var r = CaptureAll(new FakeFormat { Id = 0xC0F1, Name = "HTML Format", Data = AnalysisTests.CfHtml(frag) });
        var html = r.Formats[0].Analysis!.Html!;
        var total = html.HtmlText!.Length + html.FragmentText!.Length;
        var text = Build(r, true, [1]);
        var fenced = Regex.Matches(text, "```html\r\n(.*?)\r\n```", RegexOptions.Singleline).Sum(m => m.Groups[1].Value.Length);
        Assert.True(fenced <= Limits.ReportFormatChars, fenced.ToString());
        Assert.Equal(total > Limits.ReportFormatChars, text.Contains("レポートの1形式の上限"));
        Assert.Equal(Math.Min(total, Limits.ReportFormatChars), fenced);
    }

    // ---- R6 ----
    private static byte[] V5(byte[] after)
    {
        var h = new byte[124];
        BinaryPrimitives.WriteInt32LittleEndian(h, 124);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), 1);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(20), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(44), 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(48), 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(52), 0xFF000000);
        return [.. h, .. after];
    }

    [Fact]
    public void R6_TrailingBytesAreNotTakenAsMasks()
    {
        // 赤い画素の後に12バイトの余り（末尾4バイトは青）。
        var d = V5([0x00, 0x00, 0xFF, 0xFF, 1, 2, 3, 4, 5, 6, 7, 8, 0xFF, 0x00, 0x00, 0xFF]);
        var info = DibParser.Parse(d);
        Assert.False(info.PossibleExtraMasksAfterV5);
        Assert.Equal(12, info.ActualPixelBytes - info.ExpectedPixelBytes);
        var img = DibDecoder.Decode(d, info)!;
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, img.Bgra[..4]); // 赤（BGRA）
    }

    [Fact]
    public void R6_RealExtraMasksAreSkipped()
    {
        var masks = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(masks, 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(masks.AsSpan(4), 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(masks.AsSpan(8), 0x000000FF);
        var d = V5([.. masks, 0x00, 0xFF, 0x00, 0xFF]); // 緑の画素
        var info = DibParser.Parse(d);
        Assert.True(info.PossibleExtraMasksAfterV5);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, DibDecoder.Decode(d, info)!.Bgra[..4]);
        Assert.Equal("すべて255", info.Alpha!.Describe());
    }

    // ---- R7 ----
    [Fact]
    public void R7_TerminatorOnlyTextIsEmpty()
    {
        var r = CaptureAll(
            new FakeFormat { Id = 13, Data = [0, 0] },
            new FakeFormat { Id = 1, Data = [0] },
            new FakeFormat { Id = 7, Data = [0, 0x41, 0x42] },
            new FakeFormat { Id = 0xC300, Name = "FileNameW", Data = [0, 0, 0x41, 0] },
            new FakeFormat { Id = 0xC301, Name = "Csv", GetDataError = 1418 },
            new FakeFormat { Id = 0xC302, Name = "UniformResourceLocator", Size = 0 });
        Assert.Equal(FetchStatus.Empty, r.Formats[0].Status);
        Assert.Equal(FetchStatus.Empty, r.Formats[1].Status);
        Assert.Equal(FetchStatus.Empty, r.Formats[2].Status);
        Assert.Equal(FetchStatus.Empty, r.Formats[3].Status);
        Assert.Equal(FetchStatus.Failed, r.Formats[4].Status);
        Assert.Equal(FetchStatus.Empty, r.Formats[5].Status);
        Assert.Contains("| 空（0バイト・空文字列） |", Build(r, false));

        var nonEmpty = CaptureAll(new FakeFormat { Id = 13, Data = Encoding.Unicode.GetBytes("a\0") });
        Assert.Equal(FetchStatus.Ok, nonEmpty.Formats[0].Status);
    }
}
