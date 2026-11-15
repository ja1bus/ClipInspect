using System.ComponentModel;
using System.Runtime.InteropServices;
using ClipInspect.Core.Model;
using static ClipInspect.Core.Native.NativeMethods;

namespace ClipInspect.Core.Native;

/// <summary>
/// IClipboardApi の Win32 実装。クリップボードのデータのハンドルは解放しない（GlobalFree・DeleteObject を呼ばない）。
/// 自分で得た OpenProcess のハンドルと GetDC の DC だけを閉じる（§34 D14）。
/// </summary>
public sealed class Win32ClipboardApi : IClipboardApi
{
    public uint GetSequenceNumber() => GetClipboardSequenceNumber();

    public bool Open(out int error)
    {
        var ok = OpenClipboard(0);
        error = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }

    public void Close() => CloseClipboard();

    public uint EnumFormats(uint previous, out int error)
    {
        var id = EnumClipboardFormats(previous);
        error = id == 0 ? Marshal.GetLastPInvokeError() : 0;
        return id;
    }

    public int CountFormats() => CountClipboardFormats();

    public string? GetFormatName(uint id)
    {
        var buf = new char[256];
        var n = GetClipboardFormatNameW(id, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : null;
    }

    public nint GetOwner() => GetClipboardOwner();
    public nint GetOpenWindow() => GetOpenClipboardWindow();

    public nint GetData(uint id, out int error)
    {
        var h = GetClipboardData(id);
        error = h == 0 ? Marshal.GetLastPInvokeError() : 0;
        return h;
    }

    public GlobalRead ReadGlobal(nint handle, long limit, int partialBytes)
    {
        NativeMethods.SetLastError(0);
        var size = (long)(ulong)GlobalSize(handle);
        if (size == 0)
        {
            var err = Marshal.GetLastPInvokeError();
            return err == 0
                ? new GlobalRead { Status = FetchStatus.Empty, Size = 0, Data = [] }
                : new GlobalRead { Status = FetchStatus.SizeFailed, Error = new Win32Error(err, ErrorMessage(err)) };
        }
        var ptr = GlobalLock(handle);
        if (ptr == 0)
        {
            var err = Marshal.GetLastPInvokeError();
            return new GlobalRead { Status = FetchStatus.Failed, Size = size, Error = new Win32Error(err, "GlobalLock に失敗: " + ErrorMessage(err)) };
        }
        try
        {
            var (partial, count) = CopyPlan.Global(size, limit, partialBytes);
            var data = new byte[count];
            Marshal.Copy(ptr, data, 0, count);
            return new GlobalRead { Status = partial ? FetchStatus.SizeLimited : FetchStatus.Ok, Size = size, Data = data };
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    public BitmapHandleInfo ReadBitmap(nint handle, long pixelLimit)
    {
        if (GetObjectW(handle, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetObject(BITMAP) に失敗しました");
        int? dsBits = null;
        var isDib = GetObjectW(handle, Marshal.SizeOf<DIBSECTION>(), out DIBSECTION ds) == Marshal.SizeOf<DIBSECTION>();
        if (isDib) dsBits = ds.biBitCount;

        byte[]? pixels = null;
        string? omitted = null;
        Win32Error? pixelError = null;
        var bytes = (long)bm.bmWidth * Math.Abs(bm.bmHeight) * 4;
        if (bm.bmWidth <= 0 || bm.bmHeight == 0) omitted = "幅・高さが 0 のため画素を写しません。";
        else if (!CopyPlan.BitmapPixels(bm.bmWidth, bm.bmHeight, pixelLimit)) omitted = $"サイズ制限により画素を写しません（{bytes:N0} バイト）。";
        else
        {
            var hdc = GetDC(0);
            try
            {
                var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = bm.bmWidth, biHeight = -Math.Abs(bm.bmHeight), biPlanes = 1, biBitCount = 32, biCompression = 0 };
                var buf = new byte[bytes];
                var lines = GetDIBits(hdc, handle, 0, (uint)Math.Abs(bm.bmHeight), buf, ref bmi, 0);
                if (lines == 0)
                {
                    var err = Marshal.GetLastPInvokeError();
                    pixelError = new Win32Error(err, ErrorMessage(err));
                }
                else pixels = buf;
            }
            finally
            {
                ReleaseDC(0, hdc);
            }
        }
        return new BitmapHandleInfo
        {
            Width = bm.bmWidth,
            Height = bm.bmHeight,
            BitsPixel = bm.bmBitsPixel,
            Planes = bm.bmPlanes,
            WidthBytes = bm.bmWidthBytes,
            IsDibSection = isDib,
            DibSectionBitCount = dsBits,
            Bgra32 = pixels,
            PixelsOmittedReason = omitted,
            PixelError = pixelError,
        };
    }

    public int ReadPaletteEntries(nint handle)
    {
        if (GetObjectW(handle, 2, out ushort entries) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetObject(パレット) に失敗しました");
        return entries;
    }

    public EnhMetafileInfo ReadEnhMetafile(nint handle, long limit)
    {
        if (GetEnhMetaFileHeader(handle, (uint)Marshal.SizeOf<ENHMETAHEADER>(), out var h) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetEnhMetaFileHeader に失敗しました");
        byte[]? data = null;
        var size = GetEnhMetaFileBits(handle, 0, null);
        if (size > 0 && size <= limit)
        {
            data = new byte[size];
            GetEnhMetaFileBits(handle, size, data);
        }
        return new EnhMetafileInfo
        {
            BoundsLeft = h.rclBoundsLeft,
            BoundsTop = h.rclBoundsTop,
            BoundsRight = h.rclBoundsRight,
            BoundsBottom = h.rclBoundsBottom,
            Records = h.nRecords,
            Bytes = h.nBytes,
            Data = data,
        };
    }

    public MetafilePictInfo ReadMetafilePict(nint handle)
    {
        var ptr = GlobalLock(handle);
        if (ptr == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "GlobalLock(METAFILEPICT) に失敗しました");
        try
        {
            var mm = Marshal.ReadInt32(ptr);
            var x = Marshal.ReadInt32(ptr, 4);
            var y = Marshal.ReadInt32(ptr, 8);
            var hmf = Marshal.ReadIntPtr(ptr, IntPtr.Size == 8 ? 16 : 12);
            var bytes = hmf == 0 ? 0 : GetMetaFileBitsEx(hmf, 0, 0);
            return new MetafilePictInfo { MappingMode = mm, XExt = x, YExt = y, MetafileBytes = bytes };
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    public WindowProcessInfo GetWindowProcess(nint hwnd) => WindowInfo.Get(hwnd);

    public (int Ansi, int Oem) GetSystemCodePages() => ((int)GetACP(), (int)GetOEMCP());

    public (int Ansi, int Oem)? GetLocaleCodePages(uint lcid)
    {
        if (GetLocaleInfoW(lcid, LOCALE_IDEFAULTANSICODEPAGE | LOCALE_RETURN_NUMBER, out var ansi, 2) == 0) return null;
        if (GetLocaleInfoW(lcid, LOCALE_IDEFAULTCODEPAGE | LOCALE_RETURN_NUMBER, out var oem, 2) == 0) return null;
        return (ansi, oem);
    }

    public string ErrorMessage(int code) => new Win32Exception(code).Message;

    public void Sleep(int milliseconds) => Thread.Sleep(milliseconds);
}

/// <summary>写す量の決め方（試験の対象。§26.8）。</summary>
public static class CopyPlan
{
    /// <summary>HGLOBAL: 上限以下なら全体、超えたら先頭 partialBytes だけ。</summary>
    public static (bool Partial, int Count) Global(long size, long limit, int partialBytes) =>
        size > limit ? (true, (int)Math.Min(partialBytes, size)) : (false, (int)size);

    /// <summary>HBITMAP の画素（幅×高さ×4）を写すか。</summary>
    public static bool BitmapPixels(int width, int height, long limit) => (long)width * Math.Abs((long)height) * 4 <= limit;
}

/// <summary>ウィンドウのクラス名・スレッド・プロセスの情報（実測）。</summary>
public static class WindowInfo
{
    public static WindowProcessInfo Get(nint hwnd)
    {
        if (hwnd == 0) return new WindowProcessInfo();
        var cls = new char[256];
        var n = GetClassNameW(hwnd, cls, cls.Length);
        var tid = GetWindowThreadProcessId(hwnd, out var pid);
        if (tid == 0)
        {
            var err = Marshal.GetLastPInvokeError();
            return new WindowProcessInfo { Hwnd = hwnd, ClassName = n > 0 ? new string(cls, 0, n) : null, ProcessError = new Win32Error(err, new Win32Exception(err).Message) };
        }
        string? path = null;
        Win32Error? perr = null;
        var hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hp == 0)
        {
            var err = Marshal.GetLastPInvokeError();
            perr = new Win32Error(err, new Win32Exception(err).Message);
        }
        else
        {
            try
            {
                var buf = new char[32768];
                var size = buf.Length;
                if (QueryFullProcessImageNameW(hp, 0, buf, ref size)) path = new string(buf, 0, size);
                else
                {
                    var err = Marshal.GetLastPInvokeError();
                    perr = new Win32Error(err, new Win32Exception(err).Message);
                }
            }
            finally
            {
                CloseHandle(hp);
            }
        }
        return new WindowProcessInfo
        {
            Hwnd = hwnd,
            ClassName = n > 0 ? new string(cls, 0, n) : null,
            ThreadId = tid,
            ProcessId = pid,
            ExePath = path,
            ExeName = path is null ? null : Path.GetFileName(path),
            ProcessError = perr,
        };
    }
}
