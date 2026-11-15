using System.ComponentModel;
using ClipInspect.Core.Model;

namespace ClipInspect.Views;

/// <summary>形式一覧の1行。</summary>
public sealed class FormatRow(ClipFormat format) : INotifyPropertyChanged
{
    private bool _include;

    public ClipFormat Format { get; } = format;
    public int Index => Format.Index;
    public string Id => $"0x{Format.Id:X4}";
    public string Name => Format.Name;
    public string Kind => Labels.Of(Format.Kind);
    public string Size => Format.DataSize is { } n ? $"{n:N0} B" : Format.Bitmap is { } b ? $"{b.Width}×{b.Height}" : "";
    public string Status => Labels.Of(Format.Status);
    public string Note => Format.SynthesisNote is null ? "" : (Format.Id == 16 ? "自動追加の可能性（推定）" : "変換形式の可能性（推定）");

    /// <summary>詳細レポートに含める。</summary>
    public bool Include
    {
        get => _include;
        set
        {
            if (_include == value) return;
            _include = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Include)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        foreach (var p in new[] { nameof(Size), nameof(Status) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
