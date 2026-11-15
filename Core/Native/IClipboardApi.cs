using ClipInspect.Core.Model;

namespace ClipInspect.Core.Native;

/// <summary>
/// クリップボードの読み取りに使う Win32 の操作（試験で偽物に差し替えるための口。§25・§32）。
/// Open〜Close は同じスレッドで呼ぶ。ハンドルはクリップボードの所有物で、解放しない。
/// </summary>
public interface IClipboardApi
{
    uint GetSequenceNumber();
    /// <summary>OpenClipboard(NULL)。失敗したら error にエラー番号。</summary>
    bool Open(out int error);
    void Close();
    /// <summary>EnumClipboardFormats。0 を返したら error に GetLastError（0 なら正常な終わり）。</summary>
    uint EnumFormats(uint previous, out int error);
    int CountFormats();
    /// <summary>登録済み形式の名前（GetClipboardFormatName）。取れなければ null。</summary>
    string? GetFormatName(uint id);
    nint GetOwner();
    nint GetOpenWindow();
    /// <summary>GetClipboardData。NULL なら error にエラー番号。</summary>
    nint GetData(uint id, out int error);

    GlobalRead ReadGlobal(nint handle, long limit, int partialBytes);
    BitmapHandleInfo ReadBitmap(nint handle, long pixelLimit);
    int ReadPaletteEntries(nint handle);
    EnhMetafileInfo ReadEnhMetafile(nint handle, long limit);
    MetafilePictInfo ReadMetafilePict(nint handle);

    WindowProcessInfo GetWindowProcess(nint hwnd);
    (int Ansi, int Oem) GetSystemCodePages();
    (int Ansi, int Oem)? GetLocaleCodePages(uint lcid);

    string ErrorMessage(int code);
    void Sleep(int milliseconds);
}

/// <summary>ClipInspect からの書き込みに使う Win32 の操作（試験で差し替える口。§37 Q18）。</summary>
public interface IClipboardWriterApi
{
    uint GetSequenceNumber();
    bool Open(nint owner, out int error);
    bool Empty(out int error);
    /// <summary>GlobalAlloc したメモリにデータを写して SetClipboardData する。失敗したらメモリを解放する。</summary>
    bool SetData(uint format, ReadOnlySpan<byte> data, out int error);
    void Close();
    nint GetOwner();
    uint RegisterFormat(string name);
    string ErrorMessage(int code);
}
