namespace ClipInspect.Core.Analysis;

/// <summary>情報の1項目。Estimated は推定（§27.1）。</summary>
public sealed record InfoItem(string Label, string Value, bool Estimated = false)
{
    public override string ToString() => Estimated ? $"{Label}: {Value}（推定）" : $"{Label}: {Value}";
}

/// <summary>画像の比較に使う値（§26.6）。</summary>
public sealed class ImageSummary
{
    public int Width { get; init; }
    public int Height { get; init; }
    public string BitDepth { get; init; } = "";
    public string Alpha { get; init; } = "";
    public string Orientation { get; init; } = "";
}

/// <summary>形式ごとの解析の結果。表示とレポートは Items・Warnings・各本文を使う。</summary>
public sealed class FormatAnalysis
{
    public List<InfoItem> Items { get; } = [];
    public List<string> Warnings { get; } = [];

    /// <summary>テキストとして表示する本文（テキスト・RTFソース・ファイル一覧など）。</summary>
    public string? Text { get; set; }
    /// <summary>本文の言語（レポートのコードフェンス）。</summary>
    public string TextLanguage { get; set; } = "text";

    public TextStats? TextStats { get; set; }
    public CfHtmlInfo? Html { get; set; }
    public RtfInfo? Rtf { get; set; }
    public DibInfo? Dib { get; set; }
    public PngInfo? Png { get; set; }
    public DropFilesInfo? Files { get; set; }
    public DwordInfo? Dword { get; set; }
    public ImageSummary? Image { get; set; }
    /// <summary>署名による画像の種類（PNG・JPEG・GIF 等）。</summary>
    public string? ImageSignature { get; set; }

    public void Add(string label, string value, bool estimated = false) => Items.Add(new InfoItem(label, value, estimated));
}
