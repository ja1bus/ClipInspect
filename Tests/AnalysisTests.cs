using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ClipInspect.Core.Analysis;
using ClipInspect.Core.Model;

namespace ClipInspect.Tests;

/// <summary>解析（試験表 §4）。</summary>
public class AnalysisTests
{
    private static readonly CodePageContext Cp = new() { SystemAnsi = 1252, SystemOem = 437 };

    private static FormatAnalysis Analyze(uint id, string name, byte[] data, CodePageContext? cp = null)
    {
        var f = new ClipFormat { Id = id, Name = name, Kind = FormatCatalog.KindOf(id, name), Handle = FormatCatalog.HandleOf(id), Status = FetchStatus.Ok, Data = data, DataSize = data.Length };
        return Analyzer.Analyze(f, cp ?? Cp);
    }

    private static byte[] U16(string s) => Encoding.Unicode.GetBytes(s);

    [Fact] // T-21
    public void T21_UnicodeTextStats()
    {
        var data = U16("A\tB\r\nC\tD\r\n").Concat(new byte[6]).ToArray();
        Assert.Equal(26, data.Length);
        var a = Analyze(13, "CF_UNICODETEXT", data);
        var s = a.TextStats!;
        Assert.Equal(10, s.Utf16Units);
        Assert.Equal(10, s.CodePoints);
        Assert.Equal(2, s.Crlf);
        Assert.Equal(0, s.LfOnly);
        Assert.Equal(0, s.CrOnly);
        Assert.Equal(2, s.Tabs);
        Assert.Equal(0, s.OtherControls);
        Assert.Equal(2, s.Lines);
        Assert.True(s.EndsWithNewline);
        Assert.Equal((2, 2), s.TsvShape);
        Assert.Contains(a.Items, i => i.Label == "TSV" && i.Estimated);
        Assert.Contains(a.Items, i => i.Label == "NUL の後の確保領域" && i.Value.StartsWith("4 "));
    }

    [Fact] // T-22
    public void T22_UnicodeEdgeCases()
    {
        Assert.Equal("A\u4200", TextAnalyzer.ReadUtf16([0x41, 0, 0, 0x42, 0, 0]).Text);
        Assert.Equal(1, TextAnalyzer.Analyze(TextAnalyzer.ReadUtf16([0x00, 0xD8, 0x41, 0, 0, 0]).Text).LoneSurrogates);
        var c = TextAnalyzer.Analyze("A\u00A0B\u200BC\uFEFF");
        Assert.Equal((1, 1, 1), (c.Nbsp, c.ZeroWidth, c.Bom));
        Assert.Equal(1, TextAnalyzer.Analyze("\uFFFD").ReplacementChars);
        var e = TextAnalyzer.Analyze("😀");
        Assert.Equal((2, 1), (e.Utf16Units, e.CodePoints));
        var f = TextAnalyzer.ReadUtf16([0x41, 0, 0x42, 0]);
        Assert.Equal("AB", f.Text);
        Assert.Null(f.NulOffset);
        var g = TextAnalyzer.Analyze("a\rb\nc");
        Assert.Equal((1, 1, 0), (g.CrOnly, g.LfOnly, g.Crlf));
        Assert.Equal(1, TextAnalyzer.Analyze("a\u0007b").OtherControls);
        Assert.Equal("A", TextAnalyzer.ReadUtf16([0x41, 0, 0x42]).Text);
        foreach (var t in new[] { "A\u4200", "AB", "a\rb\nc", "😀" }) Assert.Null(TextAnalyzer.Analyze(t).TsvShape);
    }

    [Fact] // T-23
    public void T23_EmptyVsFailure()
    {
        var a = Analyze(13, "CF_UNICODETEXT", [0, 0]);
        Assert.Equal(0, a.TextStats!.Utf16Units);
        Assert.Equal("", a.Text);
    }

    [Fact] // T-24
    public void T24_CodePages()
    {
        var jp = new CodePageContext { SystemAnsi = 1252, SystemOem = 437, LocaleLcid = 0x411, LocaleAnsi = 932, LocaleOem = 932 };
        var a = Analyze(1, "CF_TEXT", [0x82, 0xA0, 0], jp);
        Assert.Equal("あ", a.Text);
        Assert.Contains(a.Items, i => i.Label == "文字コード" && i.Value.Contains("932") && i.Estimated);
        var b = Analyze(7, "CF_OEMTEXT", [0x82, 0xA0, 0], jp);
        Assert.Equal("あ", b.Text);
        Assert.Contains(b.Items, i => i.Value.Contains("OEM コードページ 932") && i.Estimated);
        var c = Analyze(1, "CF_TEXT", [0x41, 0x42, 0]);
        Assert.Equal("AB", c.Text);
        Assert.Contains(c.Items, i => i.Value.Contains("システムの ANSI") && i.Estimated);
    }

    [Fact] // T-25
    public void T25_RegisteredTextFormats()
    {
        Assert.Equal("C:\\a", Analyze(0xC001, "FileNameW", U16("C:\\a\0")).Text);
        Assert.Equal("http://x", Analyze(0xC002, "UniformResourceLocatorW", U16("http://x\0")).Text);
        Assert.Equal("a,b", Analyze(0xC003, "Csv", [0x61, 0x2C, 0x62, 0]).Text);
        Assert.Equal(FormatKind.Text, FormatCatalog.KindOf(0xC003, "Csv"));
        var u = Analyze(0xC004, "text/plain", Encoding.UTF8.GetBytes("日本語"));
        Assert.Equal("日本語", u.Text);
        Assert.Contains(u.Items, i => i.Value == "UTF-8" && i.Estimated);
    }

    [Fact] // T-26
    public void T26_ControlChars()
    {
        Assert.Equal("a→b␍␊\nc␀d[U+00A0][U+200B][U+FEFF]", ControlChars.Visualize("a\tb\r\nc\0d\u00A0\u200B\uFEFF"));
        Assert.Equal("x␍y", ControlChars.Visualize("x\ry"));
    }

    // ---- CF_HTML ----
    public static byte[] CfHtml(string frag, string? url = null, Func<string, int>? count = null, Func<string, string>? mutate = null)
    {
        count ??= s => Encoding.UTF8.GetByteCount(s);
        const string pre = "<html><body><!--StartFragment-->";
        const string post = "<!--EndFragment--></body></html>";
        var fmt = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        if (url is not null) fmt += $"SourceURL:{url}\r\n";
        var sh = count(string.Format(fmt, 0, 0, 0, 0));
        var sf = sh + count(pre);
        var ef = sf + count(frag);
        var eh = ef + count(post);
        var text = string.Format(fmt, sh, eh, sf, ef) + pre + frag + post;
        if (mutate is not null) text = mutate(text);
        return [.. Encoding.UTF8.GetBytes(text), 0];
    }

    [Fact] // T-27
    public void T27_ValidCfHtml()
    {
        var h = CfHtmlParser.Parse(CfHtml("<b>日本語ABC</b>", "https://example.com/a/b?token=xyz"));
        Assert.Empty(h.Warnings);
        Assert.Equal("0.9", h.Version);
        Assert.Equal("<b>日本語ABC</b>", h.FragmentText);
        Assert.StartsWith("<html>", h.HtmlText);
        Assert.EndsWith("</html>", h.HtmlText);
        Assert.Equal("https://example.com/a/b?token=xyz", h.SourceUrl);
        Assert.True(h.HasNul);
        Assert.Equal(0, h.BytesAfterNul);
        Assert.Equal(h.EndHtml, h.ValidLength);
    }

    [Fact] // T-28
    public void T28_CharCountOffsets()
    {
        var a = CfHtmlParser.Parse(CfHtml("<b>日本語ABC</b>", count: s => s.Length));
        Assert.Contains(a.Warnings, w => w.Contains("文字数（UTF-16 単位）で計算された可能性"));
        var b = CfHtmlParser.Parse(CfHtml("<b>ABC</b>", count: s => s.Length));
        Assert.DoesNotContain(b.Warnings, w => w.Contains("文字数"));
        Assert.Empty(b.Warnings);
    }

    [Fact] // T-29
    public void T29_BrokenHeaders()
    {
        var a = CfHtmlParser.Parse(CfHtml("<b>x</b>", mutate: t => string.Join("\r\n", t.Split("\r\n").Where(l => !l.StartsWith("StartFragment:")))));
        Assert.Contains(a.Warnings, w => w.Contains("StartFragment"));
        Assert.False(a.OffsetsUsable);
        Assert.NotNull(a.RawText);
        var b = CfHtmlParser.Parse(CfHtml("<b>x</b>", mutate: t => System.Text.RegularExpressions.Regex.Replace(t, @"StartHTML:\d+", "StartHTML:abcdefghij")));
        Assert.Contains(b.Warnings, w => w.Contains("数値ではありません"));
        var c = CfHtmlParser.Parse([.. "<b>x</b>"u8.ToArray(), 0]);
        Assert.Contains(c.Warnings, w => w.Contains("ヘッダー"));
        Assert.Equal("<b>x</b>", c.RawText);
        var d = CfHtmlParser.Parse([]);
        Assert.Equal("", d.RawText);
    }

    private static string SetHeader(string text, string key, long value) =>
        System.Text.RegularExpressions.Regex.Replace(text, key + @":-?\d+", $"{key}:{value:D10}");

    [Fact] // T-30
    public void T30_OffsetChecks()
    {
        var baseBytes = CfHtml("<b>日本語ABC</b>");
        var info = CfHtmlParser.Parse(baseBytes);
        var len = info.ValidLength;
        long sf = info.StartFragment!.Value, ef = info.EndFragment!.Value;

        CfHtmlInfo P(Func<string, string> m) => CfHtmlParser.Parse(CfHtml("<b>日本語ABC</b>", mutate: m));

        Assert.Contains(P(t => SetHeader(t, "EndHTML", len + 10)).Warnings, w => w.Contains("範囲外"));
        Assert.Contains(P(t => SetHeader(SetHeader(t, "StartFragment", ef), "EndFragment", sf)).Warnings, w => w.Contains("順序"));
        var c = P(t => t.Replace(t.Split("\r\n")[1], "StartHTML:-000000001").Replace(t.Split("\r\n")[2], "EndHTML:-000000001"));
        Assert.True(c.NoContext);
        Assert.DoesNotContain(c.Warnings, w => w.Contains("順序") || w.Contains("範囲外"));
        var d10 = P(t => SetHeader(t, "StartHTML", 10));
        Assert.Contains(d10.Warnings, w => w.Contains("ヘッダーの途中"));
        Assert.False(d10.OffsetsUsable);
        Assert.StartsWith("<html>", d10.RawText);
        Assert.Contains(P(t => t.Replace("<!--StartFragment-->", "<!--XXXXXXXXXXXXX-->")).Warnings, w => w.Contains("マーカーがありません"));
        Assert.Contains(P(t => SetHeader(t, "StartFragment", sf + 1)).Warnings, w => w.Contains("一致しません"));
        Assert.Contains(P(t => SetHeader(t, "StartFragment", sf + 4)).Warnings, w => w.Contains("UTF-8 の文字の途中"));
        var hBytes = CfHtml("<b>日本語ABC</b>");
        var idx = IndexOf(hBytes, "ABC"u8.ToArray());
        hBytes[idx] = 0xFF; hBytes[idx + 1] = 0xFE;
        Assert.Contains(CfHtmlParser.Parse(hBytes).Warnings, w => w.Contains("不正な UTF-8 が 2 バイト"));
        Assert.Contains(P(t => SetHeader(t, "EndHTML", len - 5)).Warnings, w => w.Contains("差が 5 バイト"));
    }

    [Fact] // T-30 (j)
    public void T30_SelectionValues()
    {
        // 行を足すとオフセットがずれるため、ヘッダーの長さが同じになるよう計算し直す。
        const string frag = "<b>x</b>";
        const string pre = "<html><body><!--StartFragment-->";
        const string post = "<!--EndFragment--></body></html>";
        const string fmt = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\nStartSelection:{2:D10}\r\nEndSelection:{3:D10}\r\n";
        var sh = string.Format(fmt, 0, 0, 0, 0).Length;
        var sf = sh + pre.Length;
        var ef = sf + frag.Length;
        var eh = ef + post.Length;
        var h = CfHtmlParser.Parse(Encoding.UTF8.GetBytes(string.Format(fmt, sh, eh, sf, ef) + pre + frag + post));
        Assert.Equal(sf, h.StartSelection);
        Assert.Equal(ef, h.EndSelection);
        Assert.Empty(h.Warnings);
    }

    private static int IndexOf(byte[] hay, byte[] needle) => hay.AsSpan().IndexOf(needle);

    [Fact] // T-31
    public void T31_Rtf()
    {
        var a = RtfAnalyzer.Analyze([.. @"{\rtf1\ansi\ansicpg932 "u8.ToArray(), 0x82, 0xA0, (byte)'}'], 1252);
        Assert.True(a.HasRtfHeader);
        Assert.Equal(1, a.Version);
        Assert.Equal(932, a.AnsiCodePage);
        Assert.True(a.BracesBalanced);
        Assert.True(a.HasEightBit);
        Assert.Contains("あ", a.Source);
        Assert.False(RtfAnalyzer.Analyze(@"{\rtf1 {abc}"u8, 1252).BracesBalanced);
        Assert.False(RtfAnalyzer.Analyze("hello"u8, 1252).HasRtfHeader);
        var d = RtfAnalyzer.Analyze(@"{\rtf1 x}"u8, 1252);
        Assert.True(d.DecodeCodePageEstimated);
        Assert.Equal(1252, d.DecodeCodePage);
    }

    // ---- DIB ----
    public static byte[] Dib(int w, int h, int bpp, uint compression = 0, byte[]? extra = null, int headerSize = 40, uint clrUsed = 0, byte[]? pixels = null, int pixelDelta = 0)
    {
        var stride = (w * bpp + 31) / 32 * 4;
        var header = new byte[Math.Max(headerSize, 40)];
        BinaryPrimitives.WriteInt32LittleEndian(header, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), w);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), h);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(14), (short)bpp);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), clrUsed);
        var px = pixels ?? new byte[stride * Math.Abs(h) + pixelDelta];
        return [.. header[..headerSize], .. extra ?? [], .. px];
    }

    [Fact] // T-32
    public void T32_BasicDib()
    {
        var a = DibParser.Parse(Dib(2, 2, 32));
        Assert.Equal("BITMAPINFOHEADER", a.HeaderType);
        Assert.False(a.TopDown);
        Assert.Equal(8, a.Stride);
        Assert.Equal(40, a.PixelOffset);
        Assert.Equal(16, a.ExpectedPixelBytes);
        Assert.Equal(16, a.ActualPixelBytes);
        Assert.Equal("すべて0", a.Alpha!.Describe());
        var fa = new FormatAnalysis();
        DibParser.AddItems(fa, a);
        Assert.Contains(fa.Items, i => i.Value.Contains("規格上は未使用"));
        Assert.True(DibParser.Parse(Dib(2, -2, 32)).TopDown);
        var c = DibParser.Parse(Dib(3, 1, 24));
        Assert.Equal((12, 12), (c.Stride, c.ExpectedPixelBytes));
    }

    private static byte[] Masks(params uint[] m)
    {
        var b = new byte[m.Length * 4];
        for (var i = 0; i < m.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), m[i]);
        return b;
    }

    [Fact] // T-33
    public void T33_DibVariants()
    {
        var a = DibParser.Parse(Dib(2, 2, 32, 3, Masks(0xFF0000, 0xFF00, 0xFF)));
        Assert.Equal((0xFF0000u, 52L), (a.RedMask!.Value, a.PixelOffset));
        Assert.Equal(56, DibParser.Parse(Dib(2, 2, 32, 6, Masks(0xFF0000, 0xFF00, 0xFF, 0xFF000000))).PixelOffset);
        var v5 = Dib(2, 2, 32, 3, headerSize: 124);
        // V5 のマスクをヘッダー内に入れ、ヘッダーの後に12バイトのマスクを挟む。
        var v5h = v5[..124];
        Masks(0xFF0000, 0xFF00, 0xFF, 0xFF000000).CopyTo(v5h, 40);
        var c = DibParser.Parse([.. v5h, .. Masks(0xFF0000, 0xFF00, 0xFF), .. new byte[16]]);
        Assert.True(c.PossibleExtraMasksAfterV5);
        var d = DibParser.Parse(Dib(2, 2, 8, extra: new byte[1024]));
        Assert.Equal((256, 1064L), (d.ColorTableEntries, d.PixelOffset));
        var core = new byte[12 + 4];
        BinaryPrimitives.WriteInt32LittleEndian(core, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(10), 24);
        Assert.Equal("BITMAPCOREHEADER", DibParser.Parse(core).HeaderType);
        Assert.Contains(DibParser.Parse(Dib(2, 2, 32, pixelDelta: -1)).Warnings, w => w.Contains("破損の可能性"));
        var g = DibParser.Parse(Dib(2, 2, 32, pixelDelta: 100));
        Assert.Equal(100, g.ActualPixelBytes - g.ExpectedPixelBytes);
        Assert.DoesNotContain(g.Warnings, w => w.Contains("破損"));
        Assert.NotNull(DibParser.Parse(Dib(2, 2, 32, headerSize: 20)).Error);
        Assert.NotNull(DibParser.Parse(Dib(2, 2, 32)[..10]).Error);
        var mixed = new byte[16];
        mixed[3] = 0; mixed[7] = 128; mixed[11] = 0; mixed[15] = 128;
        Assert.StartsWith("混在", DibParser.Parse(Dib(2, 2, 32, pixels: mixed)).Alpha!.Describe());
        var full = Enumerable.Repeat((byte)255, 16).ToArray();
        Assert.Equal("すべて255", DibParser.Parse(Dib(2, 2, 32, pixels: full)).Alpha!.Describe());
        var l = v5h.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(l.AsSpan(56), 0x73524742);
        BinaryPrimitives.WriteUInt32LittleEndian(l.AsSpan(108), 4);
        var li = DibParser.Parse([.. l, .. new byte[16]]);
        Assert.Equal("LCS_sRGB", li.ColorSpace);
        Assert.Equal(4u, li.Intent);
    }

    private static byte[] Png(int colorType, bool trns, int w = 1, int h = 1)
    {
        var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] body)
        {
            var len = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)body.Length);
            ms.Write(len);
            var tb = Encoding.ASCII.GetBytes(type);
            ms.Write(tb);
            ms.Write(body);
            var crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, PngParser.Crc([.. tb, .. body]));
            ms.Write(crc);
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = (byte)colorType;
        Chunk("IHDR", ihdr);
        if (trns) Chunk("tRNS", [0, 0, 0, 0, 0, 0]);
        Chunk("IDAT", [1, 2, 3, 4, 5]);
        Chunk("IEND", []);
        return ms.ToArray();
    }

    [Fact] // T-34
    public void T34_Png()
    {
        var a = PngParser.Parse(Png(6, false));
        Assert.True(a.SignatureOk);
        Assert.Equal(["IHDR", "IDAT", "IEND"], a.Chunks.Select(c => c.Type));
        Assert.All(a.Chunks, c => Assert.True(c.CrcOk));
        Assert.Equal((1, 1, 8, 6, 0), (a.Width!.Value, a.Height!.Value, a.BitDepth!.Value, a.ColorType!.Value, a.Interlace!.Value));
        Assert.Equal((1, 5L), (a.IdatCount, a.IdatBytes));
        Assert.True(a.HasIend);
        Assert.True(a.HasAlpha);
        Assert.Empty(a.Warnings);
        Assert.False(PngParser.Parse(Png(2, false)).HasAlpha);
        Assert.True(PngParser.Parse(Png(2, true)).HasAlpha);
    }

    [Fact] // T-35
    public void T35_BrokenPng()
    {
        var p = Png(6, false);
        var idat = p.AsSpan().IndexOf("IDAT"u8);
        var a = (byte[])p.Clone(); a[idat + 4] ^= 0xFF;
        Assert.Contains(PngParser.Parse(a).Warnings, w => w.Contains("IDAT") && w.Contains("CRC"));
        Assert.True(PngParser.Parse(p[..(idat + 6)]).Truncated);
        Assert.Equal(8, PngParser.Parse([.. p, .. new byte[8]]).BytesAfterIend);
        var d = (byte[])p.Clone(); d[0] = 0;
        Assert.False(PngParser.Parse(d).SignatureOk);
        var iend = p.AsSpan().IndexOf("IEND"u8) - 4;
        Assert.Contains(PngParser.Parse(p[..iend]).Warnings, w => w.Contains("IEND がありません"));
        var f = (byte[])p.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(idat - 4), 0x7FFFFFFF);
        Assert.True(PngParser.Parse(f).Truncated);
    }

    [Fact] // T-36
    public void T36_Signatures()
    {
        Assert.Equal("JPEG", Analyze(0xC010, "JFIF", [0xFF, 0xD8, 0xFF, 0xE0]).ImageSignature);
        Assert.Equal("GIF", Analyze(0xC011, "GIF", "GIF89a"u8.ToArray()).ImageSignature);
        var c = Analyze(0xC012, "image/x-test", [0, 0, 0, 0]);
        Assert.Null(c.ImageSignature);
        Assert.Contains(c.Items, i => i.Value == "不明");
    }

    [Fact] // T-37
    public void T37_DibDecoding()
    {
        // 2×2、下から上の 24bpp: 最後の行（格納の2行目）が出力の先頭行。
        var px24 = new byte[16];
        px24[8] = 10; px24[9] = 20; px24[10] = 30; // 格納2行目の左 = 画像の左上
        var dib24 = Dib(2, 2, 24, pixels: px24);
        var hash = SHA256.HashData(dib24);
        var o = DibDecoder.Decode(dib24, DibParser.Parse(dib24))!;
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, o.Bgra[..4]);
        Assert.Equal(hash, SHA256.HashData(dib24));

        var px32 = new byte[16]; px32[0] = 1; px32[1] = 2; px32[2] = 3; px32[3] = 0; // 上から下の左上、アルファ0
        var dib32 = Dib(2, -2, 32, pixels: px32);
        var d32 = DibDecoder.Decode(dib32, DibParser.Parse(dib32))!;
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, d32.Bgra[..4]);
        Assert.True(d32.AlphaZeroShownOpaque);

        var mixed = new byte[16]; mixed[3] = 128; mixed[7] = 0;
        var dm = DibDecoder.Decode(Dib(2, -2, 32, pixels: mixed), DibParser.Parse(Dib(2, -2, 32, pixels: mixed)))!;
        Assert.Equal(128, dm.Bgra[3]);
        Assert.False(dm.AlphaZeroShownOpaque);

        foreach (var bpp in new[] { 1, 4, 8 })
        {
            var table = new byte[(1 << bpp) * 4];
            table[4] = 50; table[5] = 60; table[6] = 70; // 色1
            var stride = (2 * bpp + 31) / 32 * 4;
            var p = new byte[stride * 2];
            p[0] = (byte)(1 << (8 - bpp)); // 1行目（下から上なので画像の下の行）の左 = 色1
            p[stride] = (byte)(1 << (8 - bpp)); // 2行目（画像の上の行）の左 = 色1
            var dib = Dib(2, 2, bpp, extra: table, pixels: p);
            var img = DibDecoder.Decode(dib, DibParser.Parse(dib))!;
            Assert.Equal(new byte[] { 50, 60, 70, 255 }, img.Bgra[..4]);
        }

        var p555 = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(p555, 0x7C00); // 赤
        var d555 = Dib(2, -2, 16, pixels: p555);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, DibDecoder.Decode(d555, DibParser.Parse(d555))!.Bgra[..4]);
        var p565 = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(p565, 0x07E0); // 緑
        var d565 = Dib(2, -2, 16, 3, Masks(0xF800, 0x07E0, 0x001F), pixels: p565);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, DibDecoder.Decode(d565, DibParser.Parse(d565))!.Bgra[..4]);
    }

    [Fact] // T-38
    public void T38_ImageComparison()
    {
        ClipFormat F(string n, int w, int h) => new() { Name = n, Analysis = new FormatAnalysis { Image = new ImageSummary { Width = w, Height = h } } };
        Assert.Empty(Analyzer.CompareImages([F("PNG", 100, 50), F("CF_DIB", 100, 50)]));
        Assert.Single(Analyzer.CompareImages([F("PNG", 100, 50), F("CF_DIB", 100, 49)]));
        Assert.Equal("下から上", DibParser.Summary(DibParser.Parse(Dib(2, 2, 32))).Orientation);
        Assert.Equal("上から下", DibParser.Summary(DibParser.Parse(Dib(2, -2, 32))).Orientation);
    }

    private static byte[] DropFiles(bool wide, string payload, int pFiles = 20)
    {
        var head = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(head, (uint)pFiles);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(4), 10);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(8), 20);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(16), wide ? 1 : 0);
        return [.. head, .. (wide ? Encoding.Unicode.GetBytes(payload) : Encoding.ASCII.GetBytes(payload))];
    }

    [Fact] // T-39
    public void T39_DropFiles()
    {
        var a = DropFilesParser.Parse(DropFiles(true, "C:\\a.txt\0C:\\日本語\\b.txt\0\0"), Encoding.Latin1);
        Assert.Equal(["C:\\a.txt", "C:\\日本語\\b.txt"], a.Paths);
        Assert.True(a.Wide);
        Assert.Equal((10, 20), (a.X, a.Y));
        Assert.False(a.NonClient);
        Assert.Empty(a.Warnings);
        Assert.Equal(["C:\\a.txt"], DropFilesParser.Parse(DropFiles(false, "C:\\a.txt\0\0"), Encoding.Latin1).Paths);
    }

    [Fact] // T-40
    public void T40_BrokenDropFiles()
    {
        var a = DropFilesParser.Parse(DropFiles(true, "C:\\a.txt\0C:\\日本語\\b.txt\0"), Encoding.Latin1);
        Assert.Contains(a.Warnings, w => w.Contains("終端"));
        Assert.Equal(2, a.Paths.Count);
        Assert.NotNull(DropFilesParser.Parse(DropFiles(true, "x\0\0", 1000), Encoding.Latin1).Error);
        Assert.NotNull(DropFilesParser.Parse(new byte[10], Encoding.Latin1).Error);
    }

    [Fact] // T-41
    public void T41_HexDump()
    {
        var data = new byte[] { 0x41, 0x42, 0x43, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var lines = HexDump.Format(data, 64 * 1024, out var cut).Split("\r\n");
        Assert.False(cut);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("00000000  41 42 43", lines[0]);
        Assert.EndsWith("|ABC.............|", lines[0]);
        Assert.StartsWith("00000010  0D 0E 0F 10", lines[1]);
        HexDump.Format(new byte[65536], Limits.DisplayHexBytes, out cut);
        Assert.False(cut);
        HexDump.Format(new byte[65537], Limits.DisplayHexBytes, out cut);
        Assert.True(cut);
    }

    [Fact] // T-42
    public void T42_Dwords()
    {
        Assert.Equal("コピー", Analyze(0xC020, "Preferred DropEffect", [1, 0, 0, 0]).Dword!.Meaning);
        Assert.Equal("移動", Analyze(0xC020, "Preferred DropEffect", [2, 0, 0, 0]).Dword!.Meaning);
        Assert.Equal("コピーとリンク", Analyze(0xC020, "Preferred DropEffect", [5, 0, 0, 0]).Dword!.Meaning);
        Assert.Equal(1u, Analyze(0xC021, "Paste Succeeded", [1, 0, 0, 0]).Dword!.Value);
        Assert.Contains("ja-JP", Analyze(16, "CF_LOCALE", [0x11, 4, 0, 0]).Dword!.Meaning);
        Assert.Contains("値を読めない", Analyze(0xC020, "Preferred DropEffect", [1, 0, 0]).Dword!.Meaning);
        Assert.Equal("日本", Encoding.UTF8.GetString([0xE6, 0x97, 0xA5, 0xE6, 0x9C, 0xAC]));
    }

    [Fact] // Q14
    public void UrlMasking()
    {
        Assert.Equal("https://example.com/…", UrlMasker.Mask("https://example.com/a/b?token=xyz"));
        Assert.Equal("https://example.com/…", UrlMasker.Mask("https://example.com"));
        Assert.Equal("file:…", UrlMasker.Mask("file:///C:/Users/x/a.html"));
        Assert.Equal("about:…", UrlMasker.Mask("about:blank"));
    }
}
