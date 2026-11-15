using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using ClipInspect.Core.Analysis;
using ClipInspect.Core.Capture;
using ClipInspect.Core.Model;
using ClipInspect.Core.Native;
using ClipInspect.NativeTests;

// 実際のクリップボードの試験（試験表 §6 T-54〜T-67）。利用者のクリップボードを上書きする。
if (args.Contains("--delayed-owner")) return DelayedOwner();

var api = new Win32ClipboardApi();
var inspector = new ClipboardInspector(api);
var failures = 0;
var results = new List<string>();
var now = () => DateTimeOffset.Now;

void Check(string id, bool ok, string detail)
{
    var line = $"{(ok ? "PASS" : "FAIL")} {id}: {detail}";
    results.Add(line);
    Console.WriteLine(line);
    if (!ok) failures++;
}

void Run(string id, Action body)
{
    try { body(); }
    catch (Exception ex) { Check(id, false, $"例外 {ex.GetType().Name}: {ex.Message}"); }
}

byte[] U16(string s) => [.. Encoding.Unicode.GetBytes(s), 0, 0];
uint Reg(string name) => W.RegisterClipboardFormatW(name);
(CaptureResult R, ClipFormat? F) CaptureFetch(uint? id = null)
{
    var r = inspector.Capture(now());
    inspector.Fetch(r, id is null ? r.Formats : r.Formats.Where(f => f.Id == id));
    return (r, id is null ? null : r.Formats.FirstOrDefault(f => f.Id == id));
}

using var owner = new OwnerThread();

// ---- T-54 ----
Run("T-54a", () =>
{
    W.Put(owner.Hwnd, (13, U16("CI-ネイティブ\r\n")));
    var direct = W.Enumerate();
    var (r, f) = CaptureFetch(13);
    Check("T-54a 本文", f?.Analysis?.Text == "CI-ネイティブ\r\n", $"本文 {f?.Analysis?.Text?.Replace("\r\n", "\\r\\n")}");
    Check("T-54a 順序", r.Formats.Select(x => x.Id).SequenceEqual(direct), $"一覧 {string.Join(",", r.Formats.Select(x => $"0x{x.Id:X}"))} / 直接 {string.Join(",", direct.Select(x => $"0x{x:X}"))}");
    var synth = r.Formats.Where(x => x.Id is 1 or 7 or 16).ToList();
    Check("T-54a 推定", synth.All(x => x.SynthesisNote is not null) && f!.SynthesisNote is null, string.Join(" / ", synth.Select(x => $"{x.Name}: {x.SynthesisNote}")));
});
Run("T-54b", () =>
{
    W.Put(owner.Hwnd, (1, "ABC\0"u8.ToArray()));
    var r = inspector.Capture(now());
    var u = r.Formats.FirstOrDefault(x => x.Id == 13);
    Check("T-54b", u is null || u.SynthesisNote is not null, $"CF_UNICODETEXT の注記: {u?.SynthesisNote ?? "（形式なし）"}");
});

// ---- T-55 ----
Run("T-55", () =>
{
    var html = Html("<b>日本語ABC</b>");
    var rtf = (byte[])[.. @"{\rtf1\ansi\ansicpg932 "u8.ToArray(), 0x82, 0xA0, (byte)'}', 0];
    W.Put(owner.Hwnd, (Reg("HTML Format"), html), (Reg("Rich Text Format"), rtf));
    var (r, _) = CaptureFetch();
    var h = r.Formats.First(x => x.Name == "HTML Format").Analysis!.Html!;
    Check("T-55 HTML", h.Warnings.Count == 0 && h.FragmentText == "<b>日本語ABC</b>", $"警告 {h.Warnings.Count}、Fragment {h.FragmentText}");
    var rt = r.Formats.First(x => x.Name == "Rich Text Format").Analysis!.Rtf!;
    Check("T-55 RTF", rt.HasRtfHeader && rt.AnsiCodePage == 932 && rt.BracesBalanced && rt.Source.Contains('あ'), $"ansicpg {rt.AnsiCodePage}、括弧 {rt.BracesBalanced}");
});

// ---- T-56 ----
Run("T-56", () =>
{
    var px = new byte[4 * 3 * 4];
    for (var i = 0; i < 12; i++) { px[i * 4] = 10; px[i * 4 + 1] = 20; px[i * 4 + 2] = 200; px[i * 4 + 3] = (byte)(i % 2 == 0 ? 255 : 128); }
    var dib = DibBytes(4, 3, px, 40);
    var dibv5 = DibBytes(4, 3, px, 124);
    var hbmp = DibSection(4, 3);
    void PutImages(int pngWidth)
    {
        owner.Invoke(() =>
        {
            W.OpenClipboard(owner.Hwnd);
            W.EmptyClipboard();
            W.SetClipboardData(8, W.Global(dib));
            W.SetClipboardData(17, W.Global(dibv5));
            W.SetClipboardData(Reg("PNG"), W.Global(PngBytes(pngWidth, 3)));
            W.SetClipboardData(2, DibSection(4, 3));
            W.CloseClipboard();
        });
    }
    PutImages(4);
    var (r, _) = CaptureFetch();
    var imgs = r.Formats.Where(x => x.Id is 2 or 8 or 17 || x.Name == "PNG").ToList();
    var dims = string.Join(" / ", imgs.Select(x => $"{x.Name} {x.Analysis?.Image?.Width}×{x.Analysis?.Image?.Height} {x.Analysis?.Image?.BitDepth}"));
    Check("T-56 寸法", imgs.Count == 4 && imgs.All(x => x.Analysis?.Image is { Width: 4, Height: 3 }), dims);
    var bm = r.Formats.First(x => x.Id == 2).Bitmap!;
    Check("T-56 DIBセクション", bm.IsDibSection && bm.DibSectionBitCount == 32, $"DIBセクション {bm.IsDibSection}、元のビット数 {bm.DibSectionBitCount}");
    Check("T-56 一致", Analyzer.CompareImages(r.Formats).Count == 0, "寸法の警告なし");
    PutImages(5);
    var (r2, _) = CaptureFetch();
    Check("T-56 不一致", Analyzer.CompareImages(r2.Formats).Count == 1, string.Join(" ", Analyzer.CompareImages(r2.Formats)));
    _ = hbmp;
});

// ---- T-57 ----
Run("T-57", () =>
{
    var head = new byte[20];
    head[0] = 20; head[16] = 1;
    var drop = (byte[])[.. head, .. Encoding.Unicode.GetBytes("C:\\テスト\\a.txt\0C:\\b.txt\0\0")];
    var custom = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
    W.Put(owner.Hwnd, (15, drop), (Reg("ClipInspect.NativeTest.Custom"), custom));
    var (r, _) = CaptureFetch();
    var files = r.Formats.First(x => x.Id == 15).Analysis!.Files!;
    Check("T-57a", files.Paths.SequenceEqual(["C:\\テスト\\a.txt", "C:\\b.txt"]), string.Join(" | ", files.Paths));
    var c = r.Formats.First(x => x.Name == "ClipInspect.NativeTest.Custom");
    Check("T-57b", c.Category == FormatCategory.Registered && c.DataSize >= 256 && c.Data!.Take(256).SequenceEqual(custom), $"区分 {Labels.Of(c.Category)}、サイズ {c.DataSize}");
});

// ---- T-58 ----
Run("T-58", () =>
{
    W.Put(owner.Hwnd);
    var r = inspector.Capture(now());
    Check("T-58", r.Formats.Count == 0 && r.Outcome == CaptureOutcome.Ok, $"形式 {r.Formats.Count}、状態 {Labels.Of(r.Outcome)}");
});

// ---- T-59 遅延描画 ----
Run("T-59", () =>
{
    var delayed = Reg("ClipInspect.NativeTest.Delayed");
    owner.RenderDelayMs = 200;
    owner.Render = f => f == delayed ? [1, 2, 3, 4] : U16("delayed text");
    lock (owner.Rendered) owner.Rendered.Clear();
    owner.Invoke(() =>
    {
        W.OpenClipboard(owner.Hwnd);
        W.EmptyClipboard();
        W.SetClipboardData(13, 0);
        W.SetClipboardData(delayed, 0);
        W.CloseClipboard();
    });
    var r = inspector.Capture(now());
    int rendered;
    lock (owner.Rendered) rendered = owner.Rendered.Count;
    Check("T-59 取得では描画しない", rendered == 0 && r.Formats.Where(x => x.Id is 13 || x.Id == delayed).All(x => x.Status == FetchStatus.NotFetched), $"描画 {rendered} 回");
    var d = r.Formats.First(x => x.Id == delayed);
    inspector.Fetch(r, [d]);
    List<uint> after;
    lock (owner.Rendered) after = [.. owner.Rendered];
    Check("T-59 詳細取得で描画", after.Count(x => x == delayed) == 1 && after.All(x => x == delayed) && d.Status == FetchStatus.Ok && d.Data!.Take(4).SequenceEqual(new byte[] { 1, 2, 3, 4 }) && d.FetchMilliseconds >= 200,
        $"描画 {string.Join(",", after.Select(x => $"0x{x:X}"))}、状態 {Labels.Of(d.Status)}、所要時間 {d.FetchMilliseconds:F0} ms、番号 {d.SeqBeforeGet}→{d.SeqAfterGet}");
    Check("T-59 他は未取得", r.Formats.First(x => x.Id == 13).Status == FetchStatus.NotFetched, "CF_UNICODETEXT は未取得のまま");
    owner.Render = null;
    owner.RenderDelayMs = 0;
});

// ---- T-60 使用中 ----
Run("T-60", () =>
{
    W.Put(owner.Hwnd, (13, U16("busy")), (Reg("ClipInspect.NativeTest.X"), [1]));
    var before = inspector.Capture(now());
    using var holder = new OwnerThread();
    holder.Invoke(() => W.OpenClipboard(holder.Hwnd));
    var sw = Stopwatch.StartNew();
    var a = inspector.Capture(now());
    var ms = sw.ElapsedMilliseconds;
    Check("T-60a", a.Outcome == CaptureOutcome.Failed && a.OpenError is not null && ms < 1500 && a.OpenWindow?.ProcessId == Environment.ProcessId,
        $"状態 {Labels.Of(a.Outcome)}、{a.OpenError}、{ms} ms、開いているプロセス {a.OpenWindow?.ExeName}（{a.OpenWindow?.ClassName}）");
    var rep = inspector.Fetch(before, before.Formats);
    Check("T-60b", rep.Outcome == FetchOutcome.OpenFailed && before.Formats.All(f => f.Status is FetchStatus.NotFetched or FetchStatus.Ok),
        $"詳細取得 {rep.Outcome}、状態 {string.Join(",", before.Formats.Select(f => Labels.Of(f.Status)))}");
    holder.Invoke(() => W.CloseClipboard());
    Check("T-60c", inspector.Capture(now()).Outcome == CaptureOutcome.Ok, "解除後は正常");
});

// ---- T-61 取得後の変更 ----
Run("T-61", () =>
{
    var x = Reg("ClipInspect.NativeTest.X");
    W.Put(owner.Hwnd, (13, U16("first")), (x, [1]));
    var r = inspector.Capture(now());
    var tracker = new ChangeTracker();
    tracker.Reset(r.BaselineSeq);
    inspector.Fetch(r, r.Formats.Where(f => f.Id == 13));
    tracker.MoveBaseline(r.BaselineSeq);
    W.Put(owner.Hwnd, (13, U16("second")));
    inspector.Fetch(r, r.Formats.Where(f => f.Id == x));
    var state = tracker.Observe(api.GetSequenceNumber());
    Check("T-61", r.Formats.First(f => f.Id == x).Status == FetchStatus.Unavailable && r.Formats.First(f => f.Id == 13).Analysis?.Text == "first" && state == ChangeState.ChangedByOther,
        $"登録形式 {Labels.Of(r.Formats.First(f => f.Id == x).Status)}、本文 {r.Formats.First(f => f.Id == 13).Analysis?.Text}、変更 {ChangeTracker.Label(state)}");
});

// ---- T-62 大容量 ----
Run("T-62", () =>
{
    var big = new byte[70 * 1024 * 1024 + 2];
    for (var i = 0; i < big.Length - 2; i += 2) big[i] = 0x41;
    W.Put(owner.Hwnd, (13, big));
    big = null;
    var (_, a) = CaptureFetch(13);
    Check("T-62a", a!.Status == FetchStatus.SizeLimited && a.Data!.Length == 65536 && a.DataSize >= 70L * 1024 * 1024, $"{Labels.Of(a.Status)}、写した {a.Data?.Length}、元 {a.DataSize:N0}");
    a = null;
    GC.Collect();
    var dib8k = DibBytes(7680, 4320, null, 40);
    W.Put(owner.Hwnd, (8, dib8k));
    dib8k = null;
    var (_, b) = CaptureFetch(8);
    Check("T-62b", b!.Status == FetchStatus.Ok && b.Analysis?.Dib?.Width == 7680, $"{Labels.Of(b.Status)}、{b.DataSize:N0} バイト、解析 {b.Analysis?.Dib?.Width}×{b.Analysis?.Dib?.Height}");
    b = null;
    GC.Collect();
    var dibHuge = DibBytes(10000, 8000, null, 40);
    W.Put(owner.Hwnd, (8, dibHuge));
    dibHuge = null;
    GC.Collect();
    var (_, c) = CaptureFetch(8);
    Check("T-62c", c!.Status == FetchStatus.SizeLimited, $"{Labels.Of(c.Status)}、元 {c.DataSize:N0}");
    Check("T-62 メモリ", true, $"最大のワーキングセット {Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024} MiB（記録のみ）");
    W.Put(owner.Hwnd);
    GC.Collect();
});

// ---- T-63 プライベート形式 ----
Run("T-63", () =>
{
    W.Put(owner.Hwnd, (0x200, new byte[] { 1, 2, 3 }));
    var (r, f) = CaptureFetch(0x200);
    Check("T-63", f!.Name == "CF_PRIVATEFIRST+0" && f.Category == FormatCategory.Private && f.Status == FetchStatus.Unsupported, $"{f.Name} {Labels.Of(f.Category)} {Labels.Of(f.Status)}");
});

// ---- T-64 書き込み ----
Run("T-64", () =>
{
    var writer = new ClipboardWriter(new Win32ClipboardWriterApi());
    WriteResult w = null!;
    owner.Invoke(() => w = writer.WriteAsync(owner.Hwnd, [(13u, ClipboardWriter.UnicodeText("テスト\r\nreport"))], ms => { Thread.Sleep(ms); return Task.CompletedTask; }).GetAwaiter().GetResult());
    var ids = W.Enumerate();
    var hist = Reg("CanIncludeInClipboardHistory");
    var cloud = Reg("CanUploadToCloudClipboard");
    var excl = Reg("ExcludeClipboardContentFromMonitorProcessing");
    var r = inspector.Capture(now());
    inspector.Fetch(r, r.Formats);
    var text = r.Formats.First(f => f.Id == 13).Analysis?.Text;
    var hv = r.Formats.First(f => f.Id == hist).Analysis?.Dword?.Value;
    var cv = r.Formats.First(f => f.Id == cloud).Analysis?.Dword?.Value;
    Check("T-64", w.Status == WriteStatus.Written && ids.IndexOf(hist) < ids.IndexOf(13) && ids.IndexOf(cloud) < ids.IndexOf(13) && !ids.Contains(excl)
        && hv == 0 && cv == 0 && text == "テスト\r\nreport" && W.GetClipboardOwner() == owner.Hwnd && w.Sequence == W.GetClipboardSequenceNumber(),
        $"状態 {w.Status}、順序 {string.Join(",", ids.Select(x => $"0x{x:X}"))}、値 {hv}/{cv}、番号 {w.Sequence}/{W.GetClipboardSequenceNumber()}");
});

// ---- T-65 所有者 ----
Run("T-65", () =>
{
    W.Put(owner.Hwnd, (13, U16("owner")));
    var a = inspector.Capture(now());
    Check("T-65a", a.Owner?.ClassName == owner.ClassName && a.Owner.ProcessId == Environment.ProcessId && a.Owner.ExeName is not null,
        $"クラス {a.Owner?.ClassName}、PID {a.Owner?.ProcessId}、{a.Owner?.ExeName}");
    W.Put(0, (13, U16("no owner")));
    var b = inspector.Capture(now());
    Check("T-65b", b.Owner is null, "所有ウィンドウなし");
});

// ---- T-66 ハンドルを解放しない ----
Run("T-66", () =>
{
    var px = new byte[4 * 3 * 4];
    owner.Invoke(() =>
    {
        W.OpenClipboard(owner.Hwnd);
        W.EmptyClipboard();
        W.SetClipboardData(2, DibSection(4, 3));
        W.SetClipboardData(8, W.Global(DibBytes(4, 3, px, 40)));
        W.CloseClipboard();
    });
    CaptureFetch();
    W.OpenClipboard(0);
    var hb = W.GetClipboardData(2);
    var ok1 = W.GetObjectW(hb, System.Runtime.InteropServices.Marshal.SizeOf<W.BITMAP>(), out var bm) != 0 && bm.bmWidth == 4 && bm.bmHeight == 3;
    var hd = W.GetClipboardData(8);
    var ok2 = hd != 0 && (long)W.GlobalSize(hd) >= 40 + 48;
    W.CloseClipboard();
    var (r2, _) = CaptureFetch();
    Check("T-66", ok1 && ok2 && r2.Formats.Where(f => f.Id is 2 or 8).All(f => f.Status == FetchStatus.Ok), $"直接読み取り BITMAP {ok1}、DIB {ok2}、2回目 {string.Join(",", r2.Formats.Select(f => Labels.Of(f.Status)))}");
});

// ---- T-67 番号の実測 ----
Run("T-67", () =>
{
    W.Put(owner.Hwnd, (13, U16("seq")));
    var a = inspector.Capture(now());
    Check("T-67a", a.Outcome != CaptureOutcome.Warning || a.Warnings.Count == 0, $"取得: 開く前 {a.SeqBeforeOpen} / 開いた直後 {a.SeqAfterOpen} / 閉じる直前 {a.SeqAtClose} / 閉じた後 {a.SeqAfterClose}、状態 {Labels.Of(a.Outcome)}");
    var t = a.Formats.FirstOrDefault(f => f.Id == 1);
    if (t is not null)
    {
        inspector.Fetch(a, [t]);
        Check("T-67b", true, $"CF_TEXT（合成）の詳細取得: GetClipboardData 前 {t.SeqBeforeGet} / 後 {t.SeqAfterGet}、基準 {a.BaselineSeq}、閉じた後 {api.GetSequenceNumber()}");
    }
    var delayed = Reg("ClipInspect.NativeTest.Delayed");
    owner.Render = _ => [9, 9, 9, 9];
    owner.Invoke(() => { W.OpenClipboard(owner.Hwnd); W.EmptyClipboard(); W.SetClipboardData(delayed, 0); W.CloseClipboard(); });
    var c = inspector.Capture(now());
    var d = c.Formats.First(f => f.Id == delayed);
    inspector.Fetch(c, [d]);
    Check("T-67c", d.Status == FetchStatus.Ok, $"遅延描画の詳細取得: GetClipboardData 前 {d.SeqBeforeGet} / 後 {d.SeqAfterGet}、閉じた後 {api.GetSequenceNumber()}");
    owner.Render = null;
});

W.Put(owner.Hwnd);
Console.WriteLine();
Console.WriteLine(failures == 0 ? $"すべて合格（{results.Count} 件）" : $"不合格 {failures} 件 / {results.Count} 件");
return failures == 0 ? 0 : 1;

// ================= 補助 =================

static byte[] Html(string frag)
{
    const string pre = "<html><body><!--StartFragment-->";
    const string post = "<!--EndFragment--></body></html>";
    const string fmt = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
    var u = Encoding.UTF8;
    var sh = u.GetByteCount(string.Format(fmt, 0, 0, 0, 0));
    var sf = sh + u.GetByteCount(pre);
    var ef = sf + u.GetByteCount(frag);
    var eh = ef + u.GetByteCount(post);
    return [.. u.GetBytes(string.Format(fmt, sh, eh, sf, ef) + pre + frag + post), 0];
}

static byte[] DibBytes(int w, int h, byte[]? px, int headerSize)
{
    var bytes = (long)w * h * 4;
    var buf = new byte[headerSize + (headerSize == 124 ? 0 : 0) + bytes];
    var s = buf.AsSpan();
    BinaryPrimitives.WriteInt32LittleEndian(s, headerSize);
    BinaryPrimitives.WriteInt32LittleEndian(s[4..], w);
    BinaryPrimitives.WriteInt32LittleEndian(s[8..], h);
    BinaryPrimitives.WriteInt16LittleEndian(s[12..], 1);
    BinaryPrimitives.WriteInt16LittleEndian(s[14..], 32);
    if (headerSize == 124)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 3); // BI_BITFIELDS
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(s[48..], 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(s[52..], 0xFF000000);
        BinaryPrimitives.WriteUInt32LittleEndian(s[56..], 0x73524742);
    }
    px?.CopyTo(buf, headerSize);
    return buf;
}

static nint DibSection(int w, int h)
{
    var bmi = new W.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = h, biPlanes = 1, biBitCount = 32 };
    return W.CreateDIBSection(0, ref bmi, 0, out _, 0, 0);
}

static byte[] PngBytes(int w, int h)
{
    var raw = new MemoryStream();
    for (var y = 0; y < h; y++)
    {
        raw.WriteByte(0);
        for (var x = 0; x < w; x++) raw.Write([200, 20, 10, 255]);
    }
    var z = new MemoryStream();
    using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(zs);
    var ms = new MemoryStream();
    ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    void Chunk(string t, byte[] b)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)b.Length);
        ms.Write(len);
        var tb = Encoding.ASCII.GetBytes(t);
        ms.Write(tb);
        ms.Write(b);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, PngParser.Crc([.. tb, .. b]));
        ms.Write(crc);
    }
    var ihdr = new byte[13];
    BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
    BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
    ihdr[8] = 8; ihdr[9] = 6;
    Chunk("IHDR", ihdr);
    Chunk("IDAT", z.ToArray());
    Chunk("IEND", []);
    return ms.ToArray();
}

// 画面の試験用（§37 Q10）: 3形式を遅延描画で置き、各5秒かけて描画する。Enter で終了。
static int DelayedOwner()
{
    using var o = new OwnerThread { RenderDelayMs = 5000 };
    var ids = new[] { "A", "B", "C" }.Select(n => W.RegisterClipboardFormatW($"ClipInspect.Slow.{n}")).ToArray();
    o.Render = f => [1, 2, 3, 4];
    o.Invoke(() =>
    {
        W.OpenClipboard(o.Hwnd);
        W.EmptyClipboard();
        foreach (var id in ids) W.SetClipboardData(id, 0);
        W.CloseClipboard();
    });
    Console.WriteLine("遅延描画の形式 ClipInspect.Slow.A〜C を置きました（各5秒で描画）。Enter で終了します。");
    Console.ReadLine();
    return 0;
}
