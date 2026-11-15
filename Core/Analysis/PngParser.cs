using System.Buffers.Binary;
using System.Text;

namespace ClipInspect.Core.Analysis;

public sealed record PngChunk(string Type, long Offset, uint Length, bool CrcOk);

/// <summary>PNG のチャンクの検査結果（§26.6。画素はデコードしない）。</summary>
public sealed class PngInfo
{
    public bool SignatureOk { get; set; }
    public List<PngChunk> Chunks { get; } = [];
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? BitDepth { get; set; }
    public int? ColorType { get; set; }
    public int? Interlace { get; set; }
    public bool HasTrns { get; set; }
    public bool HasIend { get; set; }
    public int IdatCount { get; set; }
    public long IdatBytes { get; set; }
    public long BytesAfterIend { get; set; }
    public bool Truncated { get; set; }
    public List<string> Warnings { get; } = [];

    public bool HasAlpha => ColorType is 4 or 6 || HasTrns;

    public static string ColorTypeName(int? t) => t switch
    {
        0 => "グレースケール（0）",
        2 => "RGB（2）",
        3 => "パレット（3）",
        4 => "グレースケール＋アルファ（4）",
        6 => "RGBA（6）",
        null => "不明",
        _ => $"不明（{t}）",
    };
}

public static class PngParser
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static PngInfo Parse(ReadOnlySpan<byte> d)
    {
        var info = new PngInfo();
        if (d.Length < 8 || !d[..8].SequenceEqual(Signature))
        {
            info.Warnings.Add("PNG の署名がありません（PNG ではないか、破損しています）。");
            return info;
        }
        info.SignatureOk = true;
        long pos = 8;
        while (pos < d.Length)
        {
            if (d.Length - pos < 12)
            {
                info.Truncated = true;
                info.Warnings.Add($"位置 {pos} でチャンクの途中で終わっています（切り詰め）。");
                break;
            }
            var len = BinaryPrimitives.ReadUInt32BigEndian(d[(int)pos..]);
            var type = Encoding.ASCII.GetString(d.Slice((int)pos + 4, 4));
            if (len > int.MaxValue || pos + 12 + len > d.Length)
            {
                info.Truncated = true;
                info.Warnings.Add($"{type} チャンク（長さ {len:N0}）の途中で終わっています（切り詰め）。");
                info.Chunks.Add(new PngChunk(type, pos, len, false));
                break;
            }
            var body = d.Slice((int)pos + 8, (int)len);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(d[(int)(pos + 8 + len)..]);
            var crcOk = Crc(d.Slice((int)pos + 4, (int)len + 4)) == crc;
            info.Chunks.Add(new PngChunk(type, pos, len, crcOk));
            if (!crcOk) info.Warnings.Add($"{type} チャンク（位置 {pos}）の CRC が一致しません。");

            switch (type)
            {
                case "IHDR" when len >= 13:
                    info.Width = BinaryPrimitives.ReadInt32BigEndian(body);
                    info.Height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    info.BitDepth = body[8];
                    info.ColorType = body[9];
                    info.Interlace = body[12];
                    break;
                case "tRNS":
                    info.HasTrns = true;
                    break;
                case "IDAT":
                    info.IdatCount++;
                    info.IdatBytes += len;
                    break;
            }
            pos += 12 + len;
            if (type == "IEND")
            {
                info.HasIend = true;
                info.BytesAfterIend = d.Length - pos;
                if (info.BytesAfterIend > 0) info.Warnings.Add($"IEND の後に {info.BytesAfterIend} バイトのデータがあります。");
                break;
            }
        }
        if (info.Chunks.Count == 0 || info.Chunks[0].Type != "IHDR") info.Warnings.Add("先頭のチャンクが IHDR ではありません。");
        if (!info.HasIend && !info.Truncated) info.Warnings.Add("IEND がありません。");
        else if (!info.HasIend) info.Warnings.Add("IEND がありません。");
        return info;
    }

    public static void AddItems(FormatAnalysis a, PngInfo p)
    {
        a.Add("署名", p.SignatureOk ? "PNG" : "PNG ではない");
        if (!p.SignatureOk)
        {
            a.Warnings.AddRange(p.Warnings);
            return;
        }
        if (p.Width is not null) a.Add("幅×高さ", $"{p.Width}×{p.Height} ピクセル");
        if (p.BitDepth is not null) a.Add("ビット深度", p.BitDepth.ToString()!);
        a.Add("色の種類", PngInfo.ColorTypeName(p.ColorType));
        if (p.Interlace is not null) a.Add("インターレース", p.Interlace == 0 ? "なし（0）" : $"Adam7（{p.Interlace}）");
        a.Add("アルファ", p.ColorType is 4 or 6 ? "あり（色の種類）" : p.HasTrns ? "あり（tRNS）" : "なし");
        a.Add("IDAT", $"{p.IdatCount} 個 / 合計 {p.IdatBytes:N0} バイト");
        a.Add("IEND", p.HasIend ? "あり" : "なし");
        a.Add("チャンク", string.Join(" ", p.Chunks.Select(c => $"{c.Type}({c.Length}{(c.CrcOk ? "" : ",CRC不一致")})")));
        a.Warnings.AddRange(p.Warnings);
    }

    public static ImageSummary? Summary(PngInfo p) => p.Width is null ? null : new ImageSummary
    {
        Width = p.Width.Value,
        Height = p.Height ?? 0,
        BitDepth = $"{p.BitDepth} bit/ch {PngInfo.ColorTypeName(p.ColorType)}",
        Alpha = p.HasAlpha ? "あり" : "なし",
        Orientation = "上から下（PNG）",
    };

    public static uint Crc(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
