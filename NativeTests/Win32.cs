using System.Runtime.InteropServices;

namespace ClipInspect.NativeTests;

/// <summary>試験でクリップボードに置くための Win32（Core の内部の宣言とは別に持つ）。</summary>
internal static class W
{
    [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] public static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetClipboardData(uint format, nint h);
    [DllImport("user32.dll", SetLastError = true)] public static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] public static extern nint GetClipboardOwner();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern uint RegisterClipboardFormatW(string name);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern nint GlobalLock(nint h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalUnlock(nint h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern nuint GlobalSize(nint h);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER bmi, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern int GetObjectW(nint h, int size, out BITMAP bm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern nint DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] public static extern bool PostMessageW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);

    public const uint GMEM_MOVEABLE = 2;
    public const uint WM_RENDERFORMAT = 0x0305, WM_RENDERALLFORMATS = 0x0306, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002;
    public static readonly nint HWND_MESSAGE = -3;

    public delegate nint WndProc(nint hwnd, uint msg, nint w, nint l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public nint bmBits; }

    public static nint Global(ReadOnlySpan<byte> data)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (nuint)Math.Max(1, data.Length));
        var p = GlobalLock(h);
        unsafe { data.CopyTo(new Span<byte>((void*)p, data.Length)); }
        GlobalUnlock(h);
        return h;
    }

    /// <summary>開いて空にし、形式を置いて閉じる（所有者は owner）。</summary>
    public static void Put(nint owner, params (uint Format, byte[] Data)[] items)
    {
        for (var i = 0; !OpenClipboard(owner); i++)
        {
            if (i > 40) throw new InvalidOperationException("クリップボードを開けません");
            Thread.Sleep(25);
        }
        try
        {
            EmptyClipboard();
            foreach (var (f, d) in items)
            {
                if (SetClipboardData(f, Global(d)) == 0) throw new InvalidOperationException($"SetClipboardData({f}) に失敗 {Marshal.GetLastPInvokeError()}");
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static List<uint> Enumerate()
    {
        var list = new List<uint>();
        if (!OpenClipboard(0)) throw new InvalidOperationException("開けません");
        try
        {
            uint f = 0;
            while ((f = EnumClipboardFormats(f)) != 0) list.Add(f);
        }
        finally { CloseClipboard(); }
        return list;
    }
}

/// <summary>メッセージを処理し続ける専用スレッドのウィンドウ（遅延描画・所有者の試験用）。</summary>
internal sealed class OwnerThread : IDisposable
{
    private static int _classCount;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private W.WndProc? _proc;
    public nint Hwnd { get; private set; }
    public string ClassName { get; } = $"ClipInspectNativeTest{Interlocked.Increment(ref _classCount)}";
    public Func<uint, byte[]?>? Render { get; set; }
    public int RenderDelayMs { get; set; }
    public List<uint> Rendered { get; } = [];
    private readonly Queue<Action> _work = new();

    public OwnerThread()
    {
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>このスレッドで処理を実行する（完了まで待つ）。</summary>
    public void Invoke(Action a)
    {
        using var done = new ManualResetEventSlim();
        Exception? error = null;
        lock (_work) _work.Enqueue(() => { try { a(); } catch (Exception ex) { error = ex; } finally { done.Set(); } });
        W.PostMessageW(Hwnd, 0x8001, 0, 0);
        done.Wait();
        if (error is not null) throw error;
    }

    private void Loop()
    {
        _proc = WndProc;
        var wc = new W.WNDCLASSEX { cbSize = Marshal.SizeOf<W.WNDCLASSEX>(), lpfnWndProc = _proc, hInstance = W.GetModuleHandleW(null), lpszClassName = ClassName };
        W.RegisterClassExW(ref wc);
        Hwnd = W.CreateWindowExW(0, ClassName, "ClipInspect NativeTest", 0, 0, 0, 0, 0, W.HWND_MESSAGE, 0, wc.hInstance, 0);
        _ready.Set();
        while (W.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            W.TranslateMessage(ref msg);
            W.DispatchMessageW(ref msg);
        }
    }

    private nint WndProc(nint hwnd, uint msg, nint w, nint l)
    {
        switch (msg)
        {
            case 0x8001:
                while (true)
                {
                    Action? a;
                    lock (_work) a = _work.Count > 0 ? _work.Dequeue() : null;
                    if (a is null) break;
                    a();
                }
                return 0;
            case W.WM_RENDERFORMAT:
            {
                var f = (uint)w;
                lock (Rendered) Rendered.Add(f);
                if (RenderDelayMs > 0) Thread.Sleep(RenderDelayMs);
                var data = Render?.Invoke(f);
                if (data is not null) W.SetClipboardData(f, W.Global(data));
                return 0;
            }
            case W.WM_RENDERALLFORMATS:
                return 0; // 終了時は描画しない（形式は消える）
        }
        return W.DefWindowProcW(hwnd, msg, w, l);
    }

    public void Dispose()
    {
        // 終了時に遅延描画の形式が残っていると WM_RENDERALLFORMATS が届くため、先に空にしておく。
        try { Invoke(() => { if (W.OpenClipboard(Hwnd)) { W.EmptyClipboard(); W.CloseClipboard(); } }); } catch { }
    }
}
