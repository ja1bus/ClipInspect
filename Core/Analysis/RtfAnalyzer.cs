using System.Text;
using System.Text.RegularExpressions;

namespace ClipInspect.Core.Analysis;

public sealed class RtfInfo
{
    public bool HasRtfHeader { get; init; }
    public int? Version { get; init; }
    public int? AnsiCodePage { get; init; }
    public bool BracesBalanced { get; init; }
    public bool HasEightBit { get; init; }
    /// <summary>ソースを文字にしたコードページと、その由来。</summary>
    public int DecodeCodePage { get; init; }
    public bool DecodeCodePageEstimated { get; init; }
    public string Source { get; init; } = "";
}

/// <summary>RTF の確認（§26.4。レンダリングはしない。ソースを文字列にするだけ）。</summary>
public static partial class RtfAnalyzer
{
    [GeneratedRegex(@"^\{\\rtf(\d*)")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"\\ansicpg(\d+)")]
    private static partial Regex AnsiCpgRegex();

    public static RtfInfo Analyze(ReadOnlySpan<byte> data, int systemAnsi)
    {
        var nul = data.IndexOf((byte)0);
        var body = nul >= 0 ? data[..nul] : data;
        var ascii = Encoding.Latin1.GetString(body);
        var header = HeaderRegex().Match(ascii);
        int? version = header.Success && int.TryParse(header.Groups[1].Value, out var v) ? v : null;
        var cpg = AnsiCpgRegex().Match(ascii);
        int? ansicpg = cpg.Success && int.TryParse(cpg.Groups[1].Value, out var c) ? c : null;

        var depth = 0;
        var balanced = true;
        for (var i = 0; i < body.Length; i++)
        {
            var b = body[i];
            if (b == '\\') { i++; continue; } // \{ \} \\ はエスケープ
            if (b == '{') depth++;
            else if (b == '}')
            {
                depth--;
                if (depth < 0) { balanced = false; break; }
            }
        }
        if (depth != 0) balanced = false;

        var hasEightBit = false;
        foreach (var b in body)
        {
            if (b >= 0x80) { hasEightBit = true; break; }
        }

        var cp = ansicpg ?? systemAnsi;
        var enc = TextAnalyzer.GetEncoding(cp) ?? TextAnalyzer.GetEncoding(systemAnsi) ?? Encoding.Latin1;
        return new RtfInfo
        {
            HasRtfHeader = header.Success,
            Version = version,
            AnsiCodePage = ansicpg,
            BracesBalanced = balanced,
            HasEightBit = hasEightBit,
            DecodeCodePage = cp,
            DecodeCodePageEstimated = ansicpg is null,
            Source = enc.GetString(body),
        };
    }
}
