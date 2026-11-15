using ClipInspect.Core.Analysis;

namespace ClipInspect.Core.Model;

/// <summary>クリップボードの1つの形式（列挙の1行）と、その取得・解析の結果。</summary>
public sealed class ClipFormat
{
    /// <summary>列挙の順（1から）。</summary>
    public int Index { get; init; }
    public uint Id { get; init; }
    public string Name { get; init; } = "";
    public bool NameUnavailable { get; init; }
    public FormatCategory Category { get; init; }
    public FormatKind Kind { get; init; }
    public HandleKind Handle { get; init; }

    /// <summary>Windows の変換形式・CF_LOCALE の自動追加の可能性（推定。§27.1）。</summary>
    public string? SynthesisNote { get; set; }

    public FetchStatus Status { get; set; } = FetchStatus.NotFetched;
    /// <summary>状態の理由（未対応・取得不可・失敗の説明）。</summary>
    public string? StatusDetail { get; set; }
    public Win32Error? Error { get; set; }

    /// <summary>データの大きさ（HGLOBAL は GlobalSize の値＝確保サイズ）。</summary>
    public long? DataSize { get; set; }
    /// <summary>写したデータ（上限内なら全体、超えたら先頭だけ）。</summary>
    public byte[]? Data { get; set; }

    public double? FetchMilliseconds { get; set; }
    public uint? SeqBeforeGet { get; set; }
    public uint? SeqAfterGet { get; set; }
    public bool SeqChangedDuringGet => SeqBeforeGet is { } b && SeqAfterGet is { } a && a != b;

    public BitmapHandleInfo? Bitmap { get; set; }
    public EnhMetafileInfo? EnhMetafile { get; set; }
    public MetafilePictInfo? MetafilePict { get; set; }
    public int? PaletteEntries { get; set; }

    public FormatAnalysis? Analysis { get; set; }
    public string? AnalysisError { get; set; }

    public bool IsFetched => Status is FetchStatus.Ok or FetchStatus.Empty or FetchStatus.SizeLimited;

    public string IdText => $"0x{Id:X4} ({Id})";
}

public sealed class ProbeItem
{
    public uint Id { get; init; }
    public string Name { get; init; } = "";
    /// <summary>OLE の TYMED（.NET の項目では null）。</summary>
    public string? Tymed { get; init; }
    /// <summary>.NET の項目で、GetFormats(false) に含まれるか（含まれず GetFormats(true) だけなら変換可能）。</summary>
    public bool? InNativeDataFormats { get; init; }
    /// <summary>ネイティブの一覧に同じIDがあるか。</summary>
    public bool InNativeList { get; set; }
}

public sealed class ProbeResult
{
    public ProbeStatus OleStatus { get; set; } = ProbeStatus.NotRun;
    public string? OleError { get; set; }
    public List<ProbeItem> Ole { get; } = [];
    public ProbeStatus NetStatus { get; set; } = ProbeStatus.NotRun;
    public string? NetError { get; set; }
    public List<ProbeItem> Net { get; } = [];
}

/// <summary>1回の［取得］の結果。次の取得まで画面に保持する。</summary>
public sealed class CaptureResult
{
    public DateTimeOffset CapturedAt { get; init; }
    public CaptureOutcome Outcome { get; set; }
    public List<string> Warnings { get; } = [];
    public List<string> Notes { get; } = [];

    public uint SeqBeforeOpen { get; set; }
    public uint SeqAfterOpen { get; set; }
    public uint SeqAtClose { get; set; }
    public uint SeqAfterClose { get; set; }
    public uint? SeqAfterProbe { get; set; }

    /// <summary>詳細取得の基準の番号（前回の Open〜Close の閉じる直前の番号）。</summary>
    public uint BaselineSeq { get; set; }
    /// <summary>詳細取得で、取得後の変更を検出した。</summary>
    public bool ChangedSinceCapture { get; set; }

    public int CountFormats { get; set; }
    public bool EnumTruncated { get; set; }
    public Win32Error? EnumError { get; set; }
    public Win32Error? OpenError { get; set; }
    /// <summary>開けなかったときに開いていたウィンドウ（GetOpenClipboardWindow）。</summary>
    public WindowProcessInfo? OpenWindow { get; set; }
    public int OpenAttempts { get; set; }

    public WindowProcessInfo? Owner { get; set; }
    public CodePageContext CodePages { get; set; } = new();
    public List<ClipFormat> Formats { get; } = [];
    public ProbeResult Probe { get; set; } = new();
}
