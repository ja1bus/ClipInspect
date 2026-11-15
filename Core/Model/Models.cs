namespace ClipInspect.Core.Model;

/// <summary>形式の区分（形式IDの範囲による。§26.1）。</summary>
public enum FormatCategory { Standard, Registered, Private, GdiObject, Unknown }

/// <summary>形式の種類（解析の方法を決める。§26.4）。</summary>
public enum FormatKind { Text, Html, Rtf, Image, Metafile, Palette, FileList, Marker, Locale, Dword, Binary, NoData }

/// <summary>GetClipboardData が返すハンドルの種類（§26.3）。</summary>
public enum HandleKind { Global, Bitmap, Palette, EnhMetafile, MetafilePict, None, Unknown }

/// <summary>形式ごとの取得状態（§27）。</summary>
public enum FetchStatus { NotFetched, Ok, Empty, Failed, SizeFailed, Unsupported, SizeLimited, Unavailable }

/// <summary>取得全体の状態。</summary>
public enum CaptureOutcome { Ok, Warning, Failed }

/// <summary>OLE・.NET の確認の結果。</summary>
public enum ProbeStatus { NotRun, Ok, Failed, Timeout }

public static class Labels
{
    public static string Of(FetchStatus s) => s switch
    {
        FetchStatus.NotFetched => "未取得",
        FetchStatus.Ok => "正常",
        FetchStatus.Empty => "空（0バイト・空文字列）",
        FetchStatus.Failed => "取得失敗",
        FetchStatus.SizeFailed => "サイズ取得失敗",
        FetchStatus.Unsupported => "未対応",
        FetchStatus.SizeLimited => "サイズ制限により一部だけ取得",
        FetchStatus.Unavailable => "取得不可（取得後に変更）",
        _ => s.ToString(),
    };

    public static string Of(CaptureOutcome o) => o switch
    {
        CaptureOutcome.Ok => "正常",
        CaptureOutcome.Warning => "警告",
        _ => "失敗（開けない）",
    };

    public static string Of(FormatCategory c) => c switch
    {
        FormatCategory.Standard => "標準",
        FormatCategory.Registered => "登録済み",
        FormatCategory.Private => "プライベート",
        FormatCategory.GdiObject => "GDIオブジェクト",
        _ => "不明",
    };

    public static string Of(FormatKind k) => k switch
    {
        FormatKind.Text => "テキスト",
        FormatKind.Html => "HTML",
        FormatKind.Rtf => "RTF",
        FormatKind.Image => "画像",
        FormatKind.Metafile => "メタファイル",
        FormatKind.Palette => "パレット",
        FormatKind.FileList => "ファイル一覧",
        FormatKind.Marker => "保護指定",
        FormatKind.Locale => "ロケール",
        FormatKind.Dword => "DWORD",
        FormatKind.NoData => "データなし",
        _ => "バイナリ",
    };

    public static string Of(ProbeStatus s) => s switch
    {
        ProbeStatus.Ok => "確認済み",
        ProbeStatus.Failed => "確認できない（失敗）",
        ProbeStatus.Timeout => "確認できない（タイムアウト）",
        _ => "未確認",
    };

    /// <summary>推定の印。</summary>
    public const string Estimated = "（推定）";
}

/// <summary>Win32 のエラー番号と説明文。</summary>
public sealed record Win32Error(int Code, string Message)
{
    public override string ToString() => $"エラー {Code}: {Message}";
}

/// <summary>ウィンドウとそのプロセスの情報（実測）。</summary>
public sealed class WindowProcessInfo
{
    public nint Hwnd { get; init; }
    public string? ClassName { get; init; }
    public uint ThreadId { get; init; }
    public uint ProcessId { get; init; }
    public string? ExeName { get; init; }
    public string? ExePath { get; init; }
    /// <summary>プロセスの情報を取れなかった理由。</summary>
    public Win32Error? ProcessError { get; init; }
}

/// <summary>CF_BITMAP 等の HBITMAP から読んだ値。</summary>
public sealed class BitmapHandleInfo
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int BitsPixel { get; init; }
    public int Planes { get; init; }
    public int WidthBytes { get; init; }
    /// <summary>DIB セクションのとき、元のビット数（GetObject(DIBSECTION)）。</summary>
    public bool IsDibSection { get; init; }
    public int? DibSectionBitCount { get; init; }
    /// <summary>GetDIBits で 32bpp・上から下に変換した画素（上限内のときだけ）。</summary>
    public byte[]? Bgra32 { get; init; }
    public string? PixelsOmittedReason { get; init; }
    public Win32Error? PixelError { get; init; }
}

public sealed class EnhMetafileInfo
{
    public int BoundsLeft { get; init; }
    public int BoundsTop { get; init; }
    public int BoundsRight { get; init; }
    public int BoundsBottom { get; init; }
    public uint Records { get; init; }
    public uint Bytes { get; init; }
    public byte[]? Data { get; init; }
}

public sealed class MetafilePictInfo
{
    public int MappingMode { get; init; }
    public int XExt { get; init; }
    public int YExt { get; init; }
    public uint MetafileBytes { get; init; }
}

/// <summary>HGLOBAL の読み取り結果。</summary>
public sealed class GlobalRead
{
    public FetchStatus Status { get; init; }
    public long Size { get; init; }
    public byte[]? Data { get; init; }
    public Win32Error? Error { get; init; }
}

/// <summary>コードページの情報（ANSI/OEM のテキストの解釈に使う。いずれも推定）。</summary>
public sealed class CodePageContext
{
    public int SystemAnsi { get; init; } = 1252;
    public int SystemOem { get; init; } = 437;
    public uint? LocaleLcid { get; init; }
    public int? LocaleAnsi { get; init; }
    public int? LocaleOem { get; init; }

    public int Ansi => LocaleAnsi ?? SystemAnsi;
    public int Oem => LocaleOem ?? SystemOem;

    public string AnsiSource => LocaleAnsi is { } ? $"CF_LOCALE 0x{LocaleLcid:X4} による" : "システムの ANSI コードページ";
    public string OemSource => LocaleOem is { } ? $"CF_LOCALE 0x{LocaleLcid:X4} による" : "システムの OEM コードページ";
}
