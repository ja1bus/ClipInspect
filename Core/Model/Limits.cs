namespace ClipInspect.Core.Model;

/// <summary>取得・表示・レポートの上限（仕様書 §26.8・§34。設定項目にはしない）。</summary>
public static class Limits
{
    public const long MiB = 1024L * 1024;

    /// <summary>画像形式（CF_DIB・CF_DIBV5・PNG・画像の登録形式）の取得上限。</summary>
    public const long ImageFetchBytes = 256 * MiB;

    /// <summary>その他の形式の取得上限。</summary>
    public const long FetchBytes = 64 * MiB;

    /// <summary>上限を超えたときに写す先頭のバイト数（ヘッダーの解析と16進数表示用）。</summary>
    public const int PartialBytes = 64 * 1024;

    /// <summary>CF_BITMAP の画素を GetDIBits で写す上限（幅×高さ×4）。</summary>
    public const long BitmapPixelBytes = 256 * MiB;

    /// <summary>画面に表示するテキストの上限（UTF-16 単位）。</summary>
    public const int DisplayTextChars = 1_000_000;

    /// <summary>画面に表示する16進数の上限。</summary>
    public const int DisplayHexBytes = 64 * 1024;

    /// <summary>プレビューする画像の画素数の上限。</summary>
    public const long PreviewPixels = 100_000_000;

    /// <summary>レポートに出す1形式の内容の上限（文字）。</summary>
    public const int ReportFormatChars = 100_000;

    /// <summary>レポートに出す16進数の上限。</summary>
    public const int ReportHexBytes = 4096;

    /// <summary>レポート全体の上限（文字。§34）。</summary>
    public const int ReportTotalChars = 300_000;

    /// <summary>列挙する形式の上限。</summary>
    public const int EnumFormats = 1000;

    /// <summary>OpenClipboard の再試行の回数と間隔。</summary>
    public const int OpenRetryCount = 10;
    public const int OpenRetryDelayMs = 25;

    /// <summary>OLE・.NET の形式の確認を打ち切る時間（§34 D2）。</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
}
