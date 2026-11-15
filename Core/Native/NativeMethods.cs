using System.Runtime.InteropServices;

namespace ClipInspect.Core.Native;

internal static class NativeMethods
{
    // ---- user32: クリップボード ----
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool OpenClipboard(nint hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] internal static extern int CountClipboardFormats();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern int GetClipboardFormatNameW(uint format, char[] name, int max);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetClipboardData(uint format, nint hMem);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] internal static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] internal static extern nint GetOpenClipboardWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern int GetClassNameW(nint hwnd, char[] name, int max);
    [DllImport("user32.dll")] internal static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint hwnd, nint hdc);

    // ---- kernel32 ----
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nuint GlobalSize(nint hMem);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint GlobalLock(nint hMem);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GlobalUnlock(nint hMem);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint GlobalFree(nint hMem);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool QueryFullProcessImageNameW(nint process, uint flags, char[] name, ref int size);
    [DllImport("kernel32.dll")] internal static extern void SetLastError(uint code);
    [DllImport("kernel32.dll")] internal static extern uint GetACP();
    [DllImport("kernel32.dll")] internal static extern uint GetOEMCP();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern int GetLocaleInfoW(uint locale, uint lcType, out int data, int size);

    internal const uint GMEM_MOVEABLE = 0x0002;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint LOCALE_IDEFAULTCODEPAGE = 0x000B;
    internal const uint LOCALE_IDEFAULTANSICODEPAGE = 0x1004;
    internal const uint LOCALE_RETURN_NUMBER = 0x20000000;

    // ---- gdi32 ----
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern int GetObjectW(nint h, int size, out BITMAP bm);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern int GetObjectW(nint h, int size, out DIBSECTION ds);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern int GetObjectW(nint h, int size, out ushort entries);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern int GetDIBits(nint hdc, nint hbm, uint start, uint lines, byte[]? bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern uint GetEnhMetaFileHeader(nint hemf, uint size, out ENHMETAHEADER header);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern uint GetEnhMetaFileBits(nint hemf, uint size, byte[]? data);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern uint GetMetaFileBitsEx(nint hmf, uint size, nint data);

    // ---- ole32 ----
    [DllImport("ole32.dll")] internal static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] internal static extern void OleUninitialize();
    [DllImport("ole32.dll")] internal static extern int OleGetClipboard(out System.Runtime.InteropServices.ComTypes.IDataObject dataObject);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
        // GetDIBits は BITMAPINFO として色表・マスクの分も書くことがあるため、余裕を持たせる。
        public uint m0, m1, m2, m3;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DIBSECTION
    {
        public BITMAP dsBm;
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
        public uint dsBitfields0, dsBitfields1, dsBitfields2;
        public nint dshSection;
        public uint dsOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ENHMETAHEADER
    {
        public uint iType, nSize;
        public int rclBoundsLeft, rclBoundsTop, rclBoundsRight, rclBoundsBottom;
        public int rclFrameLeft, rclFrameTop, rclFrameRight, rclFrameBottom;
        public uint dSignature, nVersion, nBytes, nRecords;
        public ushort nHandles, sReserved;
        public uint nDescription, offDescription, nPalEntries;
        public int szlDeviceCx, szlDeviceCy, szlMillimetersCx, szlMillimetersCy;
        public uint cbPixelFormat, offPixelFormat, bOpenGL;
        public int szlMicrometersCx, szlMicrometersCy;
    }
}
