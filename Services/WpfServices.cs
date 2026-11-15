using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipInspect.Core.Analysis;
using ClipInspect.Core.Capture;
using ClipInspect.Core.Model;

namespace ClipInspect.Services;

/// <summary>OLE と .NET（WPF）から見える形式の確認。ProbeRunner の STA スレッドで呼ぶ（データは読まない）。</summary>
public static class FormatProbe
{
    public static ProbeResult Run()
    {
        var r = new ProbeResult();
        OleProbe.Fill(r);
        try
        {
            var obj = Clipboard.GetDataObject();
            if (obj is null)
            {
                r.NetStatus = ProbeStatus.Failed;
                r.NetError = "GetDataObject が null を返しました";
            }
            else
            {
                var plain = obj.GetFormats(false).ToHashSet(StringComparer.Ordinal);
                foreach (var name in obj.GetFormats(true))
                {
                    uint id = 0;
                    try { id = (uint)DataFormats.GetDataFormat(name).Id; }
                    catch (Exception) { /* 登録できない名前は ID 0 のまま */ }
                    r.Net.Add(new ProbeItem { Id = id, Name = name, InNativeDataFormats = plain.Contains(name) });
                }
                r.NetStatus = ProbeStatus.Ok;
            }
        }
        catch (Exception ex)
        {
            r.NetStatus = ProbeStatus.Failed;
            r.NetError = ex.Message;
        }
        return r;
    }
}

/// <summary>プレビュー（§26.7）。元のデータの配列は変更しない。</summary>
public sealed class Preview
{
    public BitmapSource? Image { get; init; }
    public DecodedImage? Pixels { get; init; }
    public string Note { get; init; } = "";
}

public static class ImagePreview
{
    /// <summary>形式からプレビューを作る（背景スレッドで呼ぶ。結果は Freeze 済み）。</summary>
    public static Preview? Create(ClipFormat f)
    {
        try
        {
            if (f.Bitmap is { Bgra32: { } px } bmp)
            {
                var h = Math.Abs(bmp.Height);
                // GetDIBits の第4バイトは不定のため不透明として表示する。
                var copy = (byte[])px.Clone();
                for (var i = 3; i < copy.Length; i += 4) copy[i] = 255;
                var img = new DecodedImage { Width = bmp.Width, Height = h, Bgra = copy };
                return new Preview { Image = ToBitmap(img), Pixels = img, Note = $"プレビュー: GetDIBits で変換した画素 {img.Width}×{img.Height} ピクセル（不透明として表示）" };
            }
            if (f.Data is null || f.Status == FetchStatus.SizeLimited) return null;
            var a = f.Analysis;
            if (a?.Dib is { } dib)
            {
                if (dib.Error is null && (long)dib.Width * dib.Height > Limits.PreviewPixels)
                    return new Preview { Note = "プレビュー: サイズ制限により省略" };
                var decoded = DibDecoder.Decode(f.Data, dib);
                if (decoded is not null)
                {
                    var note = $"プレビュー: 変換した画像 {decoded.Width}×{decoded.Height} ピクセル（BGRA32）" + (decoded.AlphaZeroShownOpaque ? "。アルファがすべて0のため不透明として表示" : "");
                    return new Preview { Image = ToBitmap(decoded), Pixels = decoded, Note = note };
                }
                return DecodeWithWpf(WithFileHeader(f.Data, dib), "BMP");
            }
            // PNG は IHDR の寸法で先に上限を判定する（デコードできない巨大な画像も「サイズ制限」と区別して示す。T-88）。
            if (a?.Png is { Width: { } pw, Height: { } ph } && (long)pw * ph > Limits.PreviewPixels)
                return new Preview { Note = $"プレビュー: サイズ制限により省略（IHDR の寸法 {pw}×{ph} ピクセル）" };
            if (a?.ImageSignature is { } sig) return DecodeWithWpf(f.Data, sig);
        }
        catch (Exception ex)
        {
            return new Preview { Note = $"プレビューできません: {ex.Message}" };
        }
        return null;
    }

    private static Preview DecodeWithWpf(byte[] data, string kind)
    {
        try
        {
            // 寸法だけ先に読み、上限を超える画像はデコードしない（§34 D14）。
            using (var probe = new MemoryStream(data, writable: false))
            {
                var dec = BitmapDecoder.Create(probe, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                var fr = dec.Frames[0];
                if ((long)fr.PixelWidth * fr.PixelHeight > Limits.PreviewPixels)
                    return new Preview { Note = $"プレビュー: サイズ制限により省略（{fr.PixelWidth}×{fr.PixelHeight} ピクセル）" };
            }
            using var ms = new MemoryStream(data, writable: false);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            var w = frame.PixelWidth;
            var h = frame.PixelHeight;
            var px = new byte[(long)w * h * 4];
            frame.CopyPixels(px, w * 4, 0);
            long zero = 0, full = 0, other = 0;
            for (long i = 3; i < px.Length; i += 4)
            {
                if (px[i] == 0) zero++;
                else if (px[i] == 255) full++;
                else other++;
            }
            var stats = new AlphaStats { Zero = zero, Full = full, Other = other };
            var img = new DecodedImage { Width = w, Height = h, Bgra = px };
            return new Preview
            {
                Image = ToBitmap(img),
                Pixels = img,
                Note = $"プレビュー: {kind} をデコードした画像 {w}×{h} ピクセル。アルファ（デコード後の値）: {stats.Describe()}",
            };
        }
        catch (Exception ex)
        {
            return new Preview { Note = $"プレビューできません（{kind} としてデコードできない）: {ex.Message}" };
        }
    }

    /// <summary>BMP ファイルとして読むための複製（元の配列は変えない）。</summary>
    private static byte[] WithFileHeader(byte[] dib, DibInfo info)
    {
        var buf = new byte[14 + dib.Length];
        buf[0] = (byte)'B';
        buf[1] = (byte)'M';
        BitConverter.GetBytes(buf.Length).CopyTo(buf, 2);
        BitConverter.GetBytes((int)(14 + info.PixelOffset)).CopyTo(buf, 10);
        dib.CopyTo(buf, 14);
        return buf;
    }

    public static BitmapSource ToBitmap(DecodedImage img)
    {
        var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, img.Bgra, img.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>再コピー用の PNG（デコード後の画素から作り直す）。</summary>
    public static byte[] ToPng(DecodedImage img)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(ToBitmap(img)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}

/// <summary>書き込みの所有者にするメッセージ専用ウィンドウ（§29）。</summary>
public sealed class ClipboardOwnerWindow : IDisposable
{
    private readonly HwndSource _source = new(new HwndSourceParameters("ClipInspect.Clipboard") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 });
    public nint Handle => _source.Handle;
    public void Dispose() => _source.Dispose();
}
