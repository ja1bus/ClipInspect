using System.Buffers.Binary;
using System.Numerics;

namespace ClipInspect.Core.Analysis;

/// <summary>プレビュー用に変換した画素（BGRA32・上から下）。元のデータとは別の配列。</summary>
public sealed class DecodedImage
{
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] Bgra { get; init; } = [];
    /// <summary>アルファがすべて0のため、不透明として表示した。</summary>
    public bool AlphaZeroShownOpaque { get; init; }
}

/// <summary>非圧縮の DIB（1/4/8/16/24/32bpp）を BGRA32 に変換する（§26.7）。元の配列は変更しない。</summary>
public static class DibDecoder
{
    /// <summary>変換できなければ null（RLE・JPEG・PNG・不正なヘッダー・データ不足）。</summary>
    public static DecodedImage? Decode(ReadOnlySpan<byte> d, DibInfo info)
    {
        if (info.Error is not null || !info.Uncompressed || info.Width <= 0 || info.Height <= 0) return null;
        if ((long)info.Width * info.Height > Model.Limits.PreviewPixels) return null;
        if (info.ActualPixelBytes < info.ExpectedPixelBytes) return null;
        if (info.BitCount is not (1 or 4 or 8 or 16 or 24 or 32)) return null;

        var w = info.Width;
        var h = info.Height;
        var outBuf = new byte[(long)w * h * 4];
        var offset = info.PossibleExtraMasksAfterV5 ? info.PixelOffset + 12 : info.PixelOffset;

        // 色表
        var palette = new uint[info.ColorTableEntries];
        var tableStart = info.PixelOffset - (long)info.ColorTableEntries * info.ColorTableEntrySize;
        for (var i = 0; i < palette.Length; i++)
        {
            var p = (int)(tableStart + (long)i * info.ColorTableEntrySize);
            palette[i] = 0xFF000000u | ((uint)d[p + 2] << 16) | ((uint)d[p + 1] << 8) | d[p];
        }

        uint rm, gm, bm, am;
        if (info.Compression == DibParser.BI_RGB)
        {
            (rm, gm, bm, am) = info.BitCount switch
            {
                16 => (0x7C00u, 0x03E0u, 0x001Fu, 0u),
                32 => (0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u),
                _ => (0u, 0u, 0u, 0u),
            };
        }
        else
        {
            rm = info.RedMask ?? 0; gm = info.GreenMask ?? 0; bm = info.BlueMask ?? 0; am = info.AlphaMask ?? 0;
        }

        var allAlphaZero = true;
        for (var y = 0; y < h; y++)
        {
            var srcRow = offset + (long)(info.TopDown ? y : h - 1 - y) * info.Stride;
            var dstRow = (long)y * w * 4;
            for (var x = 0; x < w; x++)
            {
                uint argb;
                switch (info.BitCount)
                {
                    case 1:
                    case 4:
                    case 8:
                    {
                        var bitPos = (long)x * info.BitCount;
                        var b = d[(int)(srcRow + bitPos / 8)];
                        var shift = 8 - info.BitCount - (int)(bitPos % 8);
                        var idx = (b >> shift) & ((1 << info.BitCount) - 1);
                        argb = idx < palette.Length ? palette[idx] : 0xFF000000u;
                        break;
                    }
                    case 24:
                    {
                        var p = (int)(srcRow + x * 3L);
                        argb = 0xFF000000u | ((uint)d[p + 2] << 16) | ((uint)d[p + 1] << 8) | d[p];
                        break;
                    }
                    case 16:
                    {
                        uint v = BinaryPrimitives.ReadUInt16LittleEndian(d[(int)(srcRow + x * 2L)..]);
                        argb = Compose(v, rm, gm, bm, am);
                        break;
                    }
                    default:
                    {
                        var v = BinaryPrimitives.ReadUInt32LittleEndian(d[(int)(srcRow + x * 4L)..]);
                        argb = Compose(v, rm, gm, bm, am);
                        break;
                    }
                }
                if ((argb >> 24) != 0) allAlphaZero = false;
                var o = dstRow + x * 4L;
                outBuf[o] = (byte)argb;
                outBuf[o + 1] = (byte)(argb >> 8);
                outBuf[o + 2] = (byte)(argb >> 16);
                outBuf[o + 3] = (byte)(argb >> 24);
            }
        }

        // 32bpp でアルファがすべて0のときは不透明として表示する（BI_RGB の第4バイトは規格上は未使用）。
        var opaque = info.BitCount == 32 && am != 0 && allAlphaZero;
        if (opaque)
        {
            for (long i = 3; i < outBuf.Length; i += 4) outBuf[i] = 255;
        }
        return new DecodedImage { Width = w, Height = h, Bgra = outBuf, AlphaZeroShownOpaque = opaque };
    }

    private static uint Compose(uint v, uint rm, uint gm, uint bm, uint am)
    {
        var r = Channel(v, rm);
        var g = Channel(v, gm);
        var b = Channel(v, bm);
        var a = am == 0 ? 255u : Channel(v, am);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    /// <summary>マスクの値を 0〜255 に広げる。</summary>
    private static uint Channel(uint v, uint mask)
    {
        if (mask == 0) return 0;
        var shift = BitOperations.TrailingZeroCount(mask);
        var max = mask >> shift;
        var x = (v & mask) >> shift;
        return max == 255 ? x : (uint)Math.Round(x * 255.0 / max);
    }

    /// <summary>
    /// 再コピー用の 24bpp・BI_RGB・下から上の DIB（ヘッダー40バイト）を作る。アルファは白に合成する（§28.2）。
    /// </summary>
    public static byte[] ToDib24OnWhite(DecodedImage img)
    {
        var stride = ((long)img.Width * 24 + 31) / 32 * 4;
        var size = 40 + stride * img.Height;
        var buf = new byte[size];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s, 40);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], img.Width);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], img.Height);
        BinaryPrimitives.WriteInt16LittleEndian(s[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[14..], 24);
        BinaryPrimitives.WriteUInt32LittleEndian(s[20..], (uint)(stride * img.Height));
        for (var y = 0; y < img.Height; y++)
        {
            var dst = 40 + (long)(img.Height - 1 - y) * stride;
            for (var x = 0; x < img.Width; x++)
            {
                var p = ((long)y * img.Width + x) * 4;
                int a = img.Bgra[p + 3];
                for (var c = 0; c < 3; c++)
                    buf[dst + x * 3 + c] = (byte)((img.Bgra[p + c] * a + 255 * (255 - a) + 127) / 255);
            }
        }
        return buf;
    }
}
