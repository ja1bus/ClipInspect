using System.Buffers.Binary;

namespace ClipInspect.Core.Analysis;

/// <summary>DIB（CF_DIB・CF_DIBV5）の解析結果（§26.6）。</summary>
public sealed class DibInfo
{
    public int HeaderSize { get; set; }
    public string HeaderType { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public bool TopDown { get; set; }
    public int Planes { get; set; }
    public int BitCount { get; set; }
    public uint Compression { get; set; }
    public string CompressionName { get; set; } = "";
    public uint SizeImage { get; set; }
    public int XPelsPerMeter { get; set; }
    public int YPelsPerMeter { get; set; }
    public uint ClrUsed { get; set; }
    public uint ClrImportant { get; set; }
    public uint? RedMask { get; set; }
    public uint? GreenMask { get; set; }
    public uint? BlueMask { get; set; }
    public uint? AlphaMask { get; set; }
    public string? ColorSpace { get; set; }
    public uint? Intent { get; set; }
    public uint? ProfileData { get; set; }
    public uint? ProfileSize { get; set; }
    public int ColorTableEntries { get; set; }
    public int ColorTableEntrySize { get; set; }
    public long Stride { get; set; }
    public long PixelOffset { get; set; }
    public long ExpectedPixelBytes { get; set; }
    public long ActualPixelBytes { get; set; }
    public bool PossibleExtraMasksAfterV5 { get; set; }
    public AlphaStats? Alpha { get; set; }
    /// <summary>解析できなかった理由（ヘッダーの不正等）。null なら解析できた。</summary>
    public string? Error { get; set; }
    public List<string> Warnings { get; } = [];

    public bool Uncompressed => Compression is DibParser.BI_RGB or DibParser.BI_BITFIELDS or DibParser.BI_ALPHABITFIELDS;
}

public sealed class AlphaStats
{
    public long Zero { get; init; }
    public long Full { get; init; }
    public long Other { get; init; }
    public long Total => Zero + Full + Other;

    public string Describe() =>
        Total == 0 ? "画素なし"
        : Zero == Total ? "すべて0"
        : Full == Total ? "すべて255"
        : $"混在（0: {Zero:N0}、255: {Full:N0}、その他: {Other:N0}）";
}

public static class DibParser
{
    public const uint BI_RGB = 0, BI_RLE8 = 1, BI_RLE4 = 2, BI_BITFIELDS = 3, BI_JPEG = 4, BI_PNG = 5, BI_ALPHABITFIELDS = 6;

    public static string CompressionName(uint c) => c switch
    {
        BI_RGB => "BI_RGB",
        BI_RLE8 => "BI_RLE8",
        BI_RLE4 => "BI_RLE4",
        BI_BITFIELDS => "BI_BITFIELDS",
        BI_JPEG => "BI_JPEG",
        BI_PNG => "BI_PNG",
        BI_ALPHABITFIELDS => "BI_ALPHABITFIELDS",
        _ => $"不明（{c}）",
    };

    public static DibInfo Parse(ReadOnlySpan<byte> d)
    {
        var info = new DibInfo();
        if (d.Length < 4)
        {
            info.Error = $"データが {d.Length} バイトしかなく、ヘッダーを読めません。";
            return info;
        }
        var hs = BinaryPrimitives.ReadInt32LittleEndian(d);
        info.HeaderSize = hs;
        if (hs is not (12 or 40 or 52 or 56 or 64 or 108 or 124))
        {
            info.Error = $"ヘッダーの大きさ {hs} は DIB のヘッダーとして認識できません。";
            return info;
        }
        if (d.Length < hs)
        {
            info.Error = $"データ（{d.Length} バイト）がヘッダーの大きさ（{hs} バイト）より短く、ヘッダーを読めません。";
            return info;
        }

        if (hs == 12)
        {
            info.HeaderType = "BITMAPCOREHEADER";
            info.Width = BinaryPrimitives.ReadUInt16LittleEndian(d[4..]);
            info.Height = BinaryPrimitives.ReadUInt16LittleEndian(d[6..]);
            info.Planes = BinaryPrimitives.ReadUInt16LittleEndian(d[8..]);
            info.BitCount = BinaryPrimitives.ReadUInt16LittleEndian(d[10..]);
            info.Compression = BI_RGB;
            info.ColorTableEntrySize = 3;
        }
        else
        {
            info.HeaderType = hs switch { 40 => "BITMAPINFOHEADER", 108 => "BITMAPV4HEADER", 124 => "BITMAPV5HEADER", _ => $"BITMAPINFOHEADER の拡張（{hs}）" };
            info.Width = BinaryPrimitives.ReadInt32LittleEndian(d[4..]);
            var h = BinaryPrimitives.ReadInt32LittleEndian(d[8..]);
            info.TopDown = h < 0;
            info.Height = h == int.MinValue ? int.MaxValue : Math.Abs(h);
            info.Planes = BinaryPrimitives.ReadUInt16LittleEndian(d[12..]);
            info.BitCount = BinaryPrimitives.ReadUInt16LittleEndian(d[14..]);
            info.Compression = BinaryPrimitives.ReadUInt32LittleEndian(d[16..]);
            info.SizeImage = BinaryPrimitives.ReadUInt32LittleEndian(d[20..]);
            info.XPelsPerMeter = BinaryPrimitives.ReadInt32LittleEndian(d[24..]);
            info.YPelsPerMeter = BinaryPrimitives.ReadInt32LittleEndian(d[28..]);
            info.ClrUsed = BinaryPrimitives.ReadUInt32LittleEndian(d[32..]);
            info.ClrImportant = BinaryPrimitives.ReadUInt32LittleEndian(d[36..]);
            info.ColorTableEntrySize = 4;
            if (hs >= 52)
            {
                info.RedMask = BinaryPrimitives.ReadUInt32LittleEndian(d[40..]);
                info.GreenMask = BinaryPrimitives.ReadUInt32LittleEndian(d[44..]);
                info.BlueMask = BinaryPrimitives.ReadUInt32LittleEndian(d[48..]);
            }
            if (hs >= 56) info.AlphaMask = BinaryPrimitives.ReadUInt32LittleEndian(d[52..]);
            if (hs >= 108)
            {
                var cs = BinaryPrimitives.ReadUInt32LittleEndian(d[56..]);
                info.ColorSpace = ColorSpaceName(cs);
            }
            if (hs >= 124)
            {
                info.Intent = BinaryPrimitives.ReadUInt32LittleEndian(d[108..]);
                info.ProfileData = BinaryPrimitives.ReadUInt32LittleEndian(d[112..]);
                info.ProfileSize = BinaryPrimitives.ReadUInt32LittleEndian(d[116..]);
            }
        }
        info.CompressionName = CompressionName(info.Compression);

        if (info.Planes != 1) info.Warnings.Add($"プレーン数が {info.Planes} です（1 であるべき）。");
        if (info.Width <= 0 || info.Height <= 0) info.Warnings.Add($"幅・高さが 0 以下です（{info.Width}×{info.Height}）。");
        if (info.BitCount is not (0 or 1 or 4 or 8 or 16 or 24 or 32)) info.Warnings.Add($"ビット数 {info.BitCount} は DIB で使われない値です。");

        // マスク（40バイトのヘッダーで BITFIELDS のときは後続、V4/V5 はヘッダー内）。
        var maskBytes = 0;
        if (hs == 40 && info.Compression == BI_BITFIELDS) maskBytes = 12;
        else if (hs == 40 && info.Compression == BI_ALPHABITFIELDS) maskBytes = 16;
        if (maskBytes > 0)
        {
            if (d.Length < hs + maskBytes)
            {
                info.Error = "マスクの途中でデータが終わっています。";
                return info;
            }
            info.RedMask = BinaryPrimitives.ReadUInt32LittleEndian(d[40..]);
            info.GreenMask = BinaryPrimitives.ReadUInt32LittleEndian(d[44..]);
            info.BlueMask = BinaryPrimitives.ReadUInt32LittleEndian(d[48..]);
            if (maskBytes == 16) info.AlphaMask = BinaryPrimitives.ReadUInt32LittleEndian(d[52..]);
        }

        info.ColorTableEntries = info.ClrUsed != 0 ? (int)Math.Min(info.ClrUsed, 65536) : info.BitCount is > 0 and <= 8 ? 1 << info.BitCount : 0;
        info.PixelOffset = hs + maskBytes + (long)info.ColorTableEntries * info.ColorTableEntrySize;
        info.Stride = ((long)info.Width * info.BitCount + 31) / 32 * 4;
        info.ExpectedPixelBytes = info.Uncompressed ? info.Stride * info.Height : info.SizeImage;
        info.ActualPixelBytes = Math.Max(0, d.Length - info.PixelOffset);

        if (info.ActualPixelBytes < info.ExpectedPixelBytes)
        {
            info.Warnings.Add($"画素データが不足しています（破損の可能性。必要 {info.ExpectedPixelBytes:N0} バイト、実際 {info.ActualPixelBytes:N0} バイト）。");
        }
        else if (info.ActualPixelBytes > info.ExpectedPixelBytes)
        {
            // V4/V5 の後に余分な 12 バイトのマスクを書くアプリがある。余りが 12 バイトで、かつその内容がヘッダー内の
            // R・G・B のマスクと一致する場合だけ追加マスクとみなし、画素の開始位置をずらす（R6。単なる末尾の余りと区別する）。
            var extra = info.ActualPixelBytes - info.ExpectedPixelBytes;
            if (hs >= 108 && info.Compression == BI_BITFIELDS && extra == 12 && d.Length >= hs + 12
                && BinaryPrimitives.ReadUInt32LittleEndian(d[hs..]) == info.RedMask
                && BinaryPrimitives.ReadUInt32LittleEndian(d[(hs + 4)..]) == info.GreenMask
                && BinaryPrimitives.ReadUInt32LittleEndian(d[(hs + 8)..]) == info.BlueMask)
                info.PossibleExtraMasksAfterV5 = true;
        }
        if (info.Uncompressed && info.SizeImage != 0 && info.SizeImage != info.ExpectedPixelBytes)
            info.Warnings.Add($"biSizeImage（{info.SizeImage:N0}）が計算した画素データ量（{info.ExpectedPixelBytes:N0}）と違います。");

        if (info.BitCount == 32 && info.Uncompressed && info.ActualPixelBytes >= info.ExpectedPixelBytes && info.Width > 0 && info.Height > 0)
        {
            var offset = info.PossibleExtraMasksAfterV5 ? info.PixelOffset + 12 : info.PixelOffset;
            info.Alpha = CountAlpha(d, info, offset);
        }
        return info;
    }

    private static AlphaStats? CountAlpha(ReadOnlySpan<byte> d, DibInfo info, long offset)
    {
        uint mask;
        if (info.Compression == BI_RGB) mask = 0xFF000000;
        else if (info.AlphaMask is { } am && am != 0) mask = am;
        else return null;
        var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        var max = mask >> shift;
        long zero = 0, full = 0, other = 0;
        for (long y = 0; y < info.Height; y++)
        {
            var row = offset + y * info.Stride;
            for (long x = 0; x < info.Width; x++)
            {
                var p = BinaryPrimitives.ReadUInt32LittleEndian(d[(int)(row + x * 4)..]);
                var a = (p & mask) >> shift;
                if (a == 0) zero++;
                else if (a == max) full++;
                else other++;
            }
        }
        return new AlphaStats { Zero = zero, Full = full, Other = other };
    }

    private static string ColorSpaceName(uint cs) => cs switch
    {
        0 => "LCS_CALIBRATED_RGB",
        0x73524742 => "LCS_sRGB",
        0x57696E20 => "LCS_WINDOWS_COLOR_SPACE",
        0x4C494E4B => "PROFILE_LINKED",
        0x4D424544 => "PROFILE_EMBEDDED",
        _ => $"0x{cs:X8}",
    };

    public static string IntentName(uint intent) => intent switch
    {
        1 => "LCS_GM_BUSINESS",
        2 => "LCS_GM_GRAPHICS",
        4 => "LCS_GM_IMAGES",
        8 => "LCS_GM_ABS_COLORIMETRIC",
        _ => intent.ToString(),
    };

    public static void AddItems(FormatAnalysis a, DibInfo i)
    {
        if (i.Error is not null)
        {
            a.Warnings.Add($"DIB を解析できません: {i.Error}");
            return;
        }
        a.Add("ヘッダー", $"{i.HeaderType}（{i.HeaderSize} バイト）");
        a.Add("幅×高さ", $"{i.Width}×{i.Height} ピクセル");
        a.Add("上下方向", i.TopDown ? "上から下（biHeight < 0）" : "下から上（biHeight > 0）");
        a.Add("ビット数", $"{i.BitCount} bpp（プレーン {i.Planes}）");
        a.Add("圧縮", i.CompressionName);
        if (i.RedMask is not null) a.Add("マスク", $"R 0x{i.RedMask:X8} / G 0x{i.GreenMask:X8} / B 0x{i.BlueMask:X8}" + (i.AlphaMask is { } am ? $" / A 0x{am:X8}" : ""));
        if (i.ColorSpace is not null) a.Add("色空間", i.ColorSpace);
        if (i.Intent is { } it) a.Add("意図", IntentName(it));
        if (i.ProfileSize is { } ps && ps > 0) a.Add("プロファイル", $"位置 {i.ProfileData} / {ps:N0} バイト");
        a.Add("biSizeImage", i.SizeImage.ToString("N0"));
        a.Add("解像度", $"{i.XPelsPerMeter}×{i.YPelsPerMeter} ピクセル/m");
        a.Add("色表", $"{i.ColorTableEntries} 項目（biClrUsed {i.ClrUsed}）");
        a.Add("ストライド", $"{i.Stride:N0} バイト（((幅×ビット数+31)/32)×4）");
        a.Add("画素の開始位置", $"{i.PixelOffset:N0} バイト");
        a.Add("画素データ", $"必要 {i.ExpectedPixelBytes:N0} バイト / 実際 {i.ActualPixelBytes:N0} バイト");
        if (i.ActualPixelBytes > i.ExpectedPixelBytes) a.Add("画素データの後の余り", $"{i.ActualPixelBytes - i.ExpectedPixelBytes:N0} バイト");
        if (i.PossibleExtraMasksAfterV5)
            a.Add("V4/V5ヘッダーの後", $"ヘッダー内と同じマスク12バイトが続くため、追加のマスクとみなし、画素を {i.PixelOffset + 12:N0} バイト目から読みます（プレビュー・アルファの集計も同じ）", estimated: true);
        if (i.Alpha is { } al)
        {
            a.Add("アルファ", al.Describe());
            if (i.Compression == BI_RGB) a.Add("注記", "BI_RGB の32bppでは第4バイトは規格上は未使用です（透過として扱うかはアプリによる）。");
        }
        else if (i.BitCount == 32 && i.Compression != BI_RGB) a.Add("アルファ", "アルファのマスクなし");
        a.Warnings.AddRange(i.Warnings);
    }

    public static ImageSummary Summary(DibInfo i) => new()
    {
        Width = i.Width,
        Height = i.Height,
        BitDepth = $"{i.BitCount} bpp",
        Alpha = i.Alpha?.Describe() ?? (i.BitCount == 32 ? "アルファのマスクなし" : "なし"),
        Orientation = i.TopDown ? "上から下" : "下から上",
    };
}
