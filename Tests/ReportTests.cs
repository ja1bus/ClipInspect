using System.Text;
using System.Text.RegularExpressions;
using ClipInspect.Core.Analysis;
using ClipInspect.Core.Capture;
using ClipInspect.Core.Model;
using ClipInspect.Core.Native;
using ClipInspect.Core.Reports;

namespace ClipInspect.Tests;

/// <summary>レポートと書き込み（試験表 §5）。</summary>
public class ReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 20, 1, 2, TimeSpan.FromHours(9));

    private static byte[] U16(string s) => [.. Encoding.Unicode.GetBytes(s), 0, 0];

    private static byte[] Hdrop(params string[] paths)
    {
        var head = new byte[20];
        head[0] = 20;
        head[16] = 1;
        return [.. head, .. Encoding.Unicode.GetBytes(string.Join("\0", paths) + "\0\0")];
    }

    /// <summary>試験表の共通入力 R-1。</summary>
    public static CaptureResult R1(params FakeFormat[] extra)
    {
        var api = new FakeClipboardApi { Owner = 0x1234 };
        api.WindowInfo = new WindowProcessInfo { Hwnd = 0x1234, ClassName = "CLIPBRDWNDCLASS", ProcessId = 1234, ThreadId = 5, ExeName = "copy.exe", ExePath = @"C:\Apps\Secret\copy.exe" };
        api.Formats.AddRange([
            new() { Id = 13, Data = U16("CI-SECRET-TEXT-12345") },
            new() { Id = 1, Data = "CI"u8.ToArray() },
            new() { Id = 0xC0F1, Name = "HTML Format", Data = AnalysisTests.CfHtml("<b>CI-SECRET-HTML-678</b>", "https://example.com/a/b?token=xyz") },
            new() { Id = 0xC0AA, Name = "PNG", Data = PngBytes() },
            new() { Id = 15, Data = Hdrop(@"C:\Users\test\secret.txt", @"C:\Users\test\b.txt") },
            new() { Id = 0xC300, Name = "ClipInspect.Test", Data = [0xDE, 0xAD, 0xBE, 0xEF] },
            new() { Id = 0xC301, Name = "ClipInspect.Fail", GetDataError = 1418 },
        ]);
        api.Formats.AddRange(extra);
        var ins = new ClipboardInspector(api);
        var r = ins.Capture(Now);
        ins.Fetch(r, r.Formats.Where(f => f.Id != 1));
        return r;
    }

    private static byte[] PngBytes()
    {
        // 1×1 RGBA の PNG（チャンクの CRC は正しく作る）。
        var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string t, byte[] b)
        {
            var len = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)b.Length);
            ms.Write(len);
            var tb = Encoding.ASCII.GetBytes(t);
            ms.Write(tb);
            ms.Write(b);
            var crc = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, PngParser.Crc([.. tb, .. b]));
            ms.Write(crc);
        }
        Chunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]);
        Chunk("IDAT", [1]);
        Chunk("IEND", []);
        return ms.ToArray();
    }

    private static string Build(CaptureResult r, bool detailed, IEnumerable<int>? include = null, bool paths = false, bool url = false, bool proc = false) =>
        ReportBuilder.Build(r, new ReportOptions { Detailed = detailed, IncludedIndexes = (include ?? []).ToHashSet(), IncludePaths = paths, IncludeFullSourceUrl = url, IncludeProcessPath = proc },
            "1.0.0", r.SeqAfterClose, ChangeState.Unchanged);

    private static int IndexOfName(CaptureResult r, string name) => r.Formats.First(f => f.Name == name).Index;

    [Fact] // T-43
    public void T43_SummaryStructure()
    {
        var text = Build(R1(), false);
        Assert.StartsWith("# ClipInspect 概要レポート\r\n", text);
        var heads = Regex.Matches(text, "^## (.+)\r$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["基本情報", "クリップボードの所有者（実測）", "形式一覧", "OLE・.NET から見える形式", "保護指定", "画像の比較", "形式ごとの情報", "取得できなかった情報・省略", "注記"], heads);
        Assert.Contains("- ClipInspect: 1.0.0", text);
        Assert.Matches(@"取得日時: \d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}", text);
        Assert.Contains("| # | ID | 名前 | 区分 | 種類 | サイズ | 状態 | 注記 |", text);
        Assert.Contains("| 1 | 0x000D (13) | CF_UNICODETEXT |", text);
        Assert.Contains("| PNG | 1 | 1 |", text);
        Assert.Matches(@"ClipInspect\.Fail.*エラー 1418", text);
        Assert.DoesNotContain("Basic information", text);
    }

    [Fact] // T-44
    public void T44_SummaryExcludesContent()
    {
        var text = Build(R1(), false);
        foreach (var s in new[] { "CI-SECRET-TEXT-12345", "CI-SECRET-HTML-678", "token=xyz", "/a/b", "secret.txt", @"C:\Users\test", @"C:\Apps\Secret", "DE AD BE EF", "DEADBEEF" })
            Assert.DoesNotContain(s, text);
        Assert.Contains("https://example.com/…", text);
        Assert.Contains("copy.exe (PID 1234)", text);
        Assert.Contains("ファイル数: 2", text);
    }

    [Fact] // T-45
    public void T45_DetailedIncludesChecked()
    {
        var r = R1();
        var inc = new[] { IndexOfName(r, "CF_UNICODETEXT"), IndexOfName(r, "HTML Format"), IndexOfName(r, "ClipInspect.Test") };
        var text = Build(r, true, inc);
        Assert.StartsWith("# ClipInspect 詳細レポート\r\n", text);
        Assert.Matches(@"```text\r\nCI-SECRET-TEXT-12345\r\n```", text);
        Assert.Matches(@"```html\r\n<b>CI-SECRET-HTML-678</b>\r\n```", text);
        Assert.Matches(@"```hex\r\n00000000  DE AD BE EF", text);
        Assert.Contains("https://example.com/…", text);
        Assert.DoesNotContain("token=xyz", text);
        var none = Build(r, true);
        Assert.DoesNotContain("CI-SECRET-TEXT-12345", none);
        Assert.DoesNotContain("DE AD BE EF", none);
    }

    [Fact] // T-46
    public void T46_PathOptions()
    {
        var r = R1(new FakeFormat { Id = 0xC400, Name = "FileNameW", Data = U16(@"C:\Users\test\secret.txt") },
            new FakeFormat { Id = 0xC401, Name = "UniformResourceLocatorW", Data = U16("https://intra/secret.txt") },
            new FakeFormat { Id = 0xC402, Name = "Shell IDList Array", Data = [1, 2, 3, 4] },
            new FakeFormat { Id = 0xC403, Name = "FileGroupDescriptorW", Data = [5, 6, 7, 8] });
        var inc = r.Formats.Where(f => f.Id is 15 or >= 0xC400).Select(f => f.Index).ToList();
        var a = Build(r, true, inc);
        Assert.DoesNotContain("secret.txt", a);
        Assert.Equal(5, Regex.Matches(a, "オプション「ファイルのパス・URL を含める」がオフ").Count);
        Assert.Contains("secret.txt", Build(r, true, inc, paths: true));
        Assert.Contains("https://example.com/a/b?token=xyz", Build(r, true, inc, url: true));
        Assert.DoesNotContain("token=xyz", Build(r, true, inc, paths: true));
        Assert.Contains(@"C:\Apps\Secret\copy.exe", Build(r, true, inc, proc: true));
        Assert.DoesNotContain(@"C:\Apps\Secret\copy.exe", Build(r, true, inc, paths: true));
    }

    [Fact] // T-47
    public void T47_TableAndFences()
    {
        Assert.Equal(@"My\|Format X", ReportBuilder.Cell("My|Format\r\nX"));
        var r = R1(new FakeFormat { Id = 0xC500, Name = "My|Format", Data = [1] });
        Assert.Contains(@"| My\|Format |", Build(r, false));
        Assert.Equal(4, ReportBuilder.LongestBacktickRun("a````b"));
        string Fence(string body)
        {
            var rr = R1(new FakeFormat { Id = 0xC501, Name = "text/plain", Data = Encoding.UTF8.GetBytes(body) });
            var idx = rr.Formats.First(f => f.Id == 0xC501).Index;
            var t = Build(rr, true, [idx]);
            return Regex.Match(t, "(`{3,})text\r\n" + Regex.Escape(body)).Groups[1].Value;
        }
        Assert.Equal(5, Fence("x````y").Length);
        Assert.Equal(3, Fence("plain").Length);
        Assert.Equal(4, Fence("a```b").Length);
    }

    [Fact] // T-48
    public void T48_ContentPreserved()
    {
        const string body = "日本語\t\t\r\n<&>\"'|`\u00A0😀\r\n2行目";
        var r = R1(new FakeFormat { Id = 0xC600, Name = "text/plain", Data = Encoding.UTF8.GetBytes(body) });
        var idx = r.Formats.First(f => f.Id == 0xC600).Index;
        var text = Build(r, true, [idx]);
        Assert.Contains("```text\r\n" + body + "\r\n```", text);
        var structure = text.Replace(body, "");
        Assert.DoesNotMatch("[^\r]\n", structure);
        var bytes = ClipboardWriter.UnicodeText(text);
        Assert.Equal(text, Encoding.Unicode.GetString(bytes, 0, bytes.Length - 2));
    }

    [Fact] // T-49
    public void T49_PerFormatLimits()
    {
        string Gen(int n) => new('x', n);
        var r = R1(new FakeFormat { Id = 0xC700, Name = "text/plain", Data = Encoding.UTF8.GetBytes(Gen(100_000)) },
            new FakeFormat { Id = 0xC701, Name = "text/plain", Data = Encoding.UTF8.GetBytes(Gen(100_001)) },
            new FakeFormat { Id = 0xC702, Name = "Bin4096", Data = new byte[4096] },
            new FakeFormat { Id = 0xC703, Name = "Bin4097", Data = new byte[4097] });
        string Section(uint id)
        {
            var idx = r.Formats.First(f => f.Id == id).Index;
            return Build(r, true, [idx]);
        }
        Assert.DoesNotContain("省略: 先頭 100,000 文字", Section(0xC700));
        Assert.Contains("残り 1 文字を省略", Section(0xC701));
        Assert.DoesNotContain("先頭 4,096 バイトだけを出力", Section(0xC702));
        Assert.Contains("先頭 4,096 バイトだけを出力しました（全体 4,097 バイト", Section(0xC703));
    }

    [Fact] // T-50
    public void T50_TotalLimit()
    {
        var big = Encoding.UTF8.GetBytes(new string('y', 90_000));
        var r = R1(Enumerable.Range(0, 4).Select(i => new FakeFormat { Id = (uint)(0xC800 + i), Name = "text/plain", Data = big }).ToArray());
        var inc = r.Formats.Where(f => f.Id >= 0xC800).Select(f => f.Index).ToList();
        var text = Build(r, true, inc);
        Assert.True(text.Length <= Limits.ReportTotalChars, text.Length.ToString());
        Assert.Equal(3, Regex.Matches(text, "y{90000}").Count);
        Assert.Contains($"{inc[3]}. text/plain: レポート全体の上限", text);
    }

    [Fact] // T-51
    public void T51_EstimatesMarked()
    {
        var r = R1(new FakeFormat { Id = 0xC900, Name = "CanIncludeInClipboardHistory", Data = [0, 0, 0, 0] });
        var text = Build(r, false);
        Assert.Contains("Windowsの変換形式の可能性（CF_UNICODETEXT から）（推定）", text);
        Assert.Contains("Windowsの規約に基づく解釈", text);
        Assert.Contains("一致するとは限りません", text);

        var api = new FakeClipboardApi { Owner = 0x55, WindowInfo = new WindowProcessInfo { Hwnd = 0x55, ProcessId = 9, ProcessError = new Win32Error(5, "アクセスが拒否されました。") } };
        api.Formats.Add(new FakeFormat { Id = 13, Data = U16("x") });
        var r2 = new ClipboardInspector(api).Capture(Now);
        var t2 = Build(r2, false);
        Assert.Contains("コピー元プロセスの情報: 取得できない（エラー 5: アクセスが拒否されました。）", t2);
        Assert.Contains("一致するとは限りません", t2);
    }

    [Fact] // T-52
    public void T52_JapaneseHeadings()
    {
        var text = Build(R1(), true, [1]);
        foreach (Match m in Regex.Matches(text, "^#+ (.+)\r$", RegexOptions.Multiline))
            Assert.Matches(@"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]|^\d+\. ", m.Groups[1].Value);
        Assert.Contains("| 正常 |", text);
        Assert.Contains("| 未取得 |", text);
    }

    private sealed class FakeWriter : IClipboardWriterApi
    {
        public List<uint> Set { get; } = [];
        public uint FailOn { get; init; }
        public bool Emptied { get; private set; }
        public uint GetSequenceNumber() => 7;
        public bool Open(nint owner, out int error) { error = 0; return true; }
        public bool Empty(out int error) { error = 0; Emptied = true; return true; }
        public bool SetData(uint format, ReadOnlySpan<byte> data, out int error)
        {
            if (format == FailOn) { error = 87; return false; }
            Set.Add(format); error = 0; return true;
        }
        public void Close() { }
        public nint GetOwner() => 0x10;
        public uint RegisterFormat(string name) => name switch
        {
            "CanIncludeInClipboardHistory" => 0xC001,
            "CanUploadToCloudClipboard" => 0xC002,
            _ => 0xC0FF,
        };
        public string ErrorMessage(int code) => "x";
    }

    [Fact] // T-53
    public async Task T53_WriterProtection()
    {
        var ok = new FakeWriter();
        var w = await new ClipboardWriter(ok).WriteAsync(0x10, [(13u, ClipboardWriter.UnicodeText("a"))], _ => Task.CompletedTask);
        Assert.Equal(WriteStatus.Written, w.Status);
        Assert.Equal([0xC001u, 0xC002u, 13u], ok.Set);

        var fail = new FakeWriter { FailOn = 0xC002 };
        var w2 = await new ClipboardWriter(fail).WriteAsync(0x10, [(13u, ClipboardWriter.UnicodeText("a"))], _ => Task.CompletedTask);
        Assert.Equal(WriteStatus.ProtectionFailed, w2.Status);
        Assert.DoesNotContain(13u, fail.Set);
        Assert.Contains("コピーできませんでした", w2.Message);
    }
}
