using ClipInspect.Core.Model;
using ClipInspect.Core.Native;

namespace ClipInspect.Tests;

/// <summary>試験用の形式。</summary>
public sealed class FakeFormat
{
    public uint Id { get; init; }
    public string? Name { get; init; }
    public byte[]? Data { get; init; }
    /// <summary>GetClipboardData が NULL を返すときのエラー番号。</summary>
    public int? GetDataError { get; init; }
    /// <summary>GlobalSize の値を偽る（Data を使わない）。</summary>
    public long? Size { get; init; }
    public int SizeError { get; init; }
    public bool ThrowOnRead { get; init; }
    public BitmapHandleInfo? Bitmap { get; init; }
    /// <summary>GetClipboardData の前後で番号を上げる（描画の想定）。</summary>
    public bool BumpSeqOnGet { get; init; }
}

/// <summary>偽の IClipboardApi。呼び出しを記録する。</summary>
public sealed class FakeClipboardApi : IClipboardApi
{
    public List<FakeFormat> Formats { get; } = [];
    public Queue<uint> Seqs { get; } = new();
    public uint Seq { get; set; } = 100;
    public Func<int, bool> OpenSucceeds { get; set; } = _ => true;
    public int OpenError { get; set; } = 5;
    public int OpenCalls { get; private set; }
    public int? EnumErrorAfter { get; set; }
    public int EnumError { get; set; }
    public List<uint> GetDataCalls { get; } = [];
    public List<long> ReadLimits { get; } = [];
    public bool IsOpen { get; private set; }
    public int OpenSessions { get; private set; }
    public nint Owner { get; set; }
    public nint OpenWindow { get; set; }
    public WindowProcessInfo? WindowInfo { get; set; }
    public Dictionary<uint, (int, int)> LocaleCodePages { get; } = new() { [0x0411] = (932, 932) };
    public int Sleeps { get; private set; }

    public uint GetSequenceNumber()
    {
        if (Seqs.Count > 0) Seq = Seqs.Dequeue();
        return Seq;
    }

    public bool Open(out int error)
    {
        OpenCalls++;
        if (OpenSucceeds(OpenCalls))
        {
            IsOpen = true;
            OpenSessions++;
            error = 0;
            return true;
        }
        error = OpenError;
        return false;
    }

    public void Close() => IsOpen = false;

    public uint EnumFormats(uint previous, out int error)
    {
        error = 0;
        var idx = previous == 0 ? 0 : Formats.FindIndex(f => f.Id == previous) + 1;
        if (EnumErrorAfter is { } n && idx >= n) { error = EnumError; return 0; }
        return idx < Formats.Count ? Formats[idx].Id : 0;
    }

    public int CountFormats() => Formats.Count;
    public string? GetFormatName(uint id) => Formats.FirstOrDefault(f => f.Id == id)?.Name;
    public nint GetOwner() => Owner;
    public nint GetOpenWindow() => OpenWindow;

    public nint GetData(uint id, out int error)
    {
        Assert.True(IsOpen, "GetData はクリップボードを開いている間に呼ぶ");
        GetDataCalls.Add(id);
        var f = Formats.First(x => x.Id == id);
        if (f.ThrowOnRead) throw new InvalidOperationException("偽の例外");
        if (f.BumpSeqOnGet) Seq++;
        if (f.GetDataError is { } e) { error = e; return 0; }
        error = 0;
        return Formats.IndexOf(f) + 1;
    }

    private FakeFormat ByHandle(nint h) => Formats[(int)h - 1];

    public GlobalRead ReadGlobal(nint handle, long limit, int partialBytes)
    {
        ReadLimits.Add(limit);
        var f = ByHandle(handle);
        var size = f.Size ?? f.Data?.Length ?? 0;
        if (size == 0)
            return f.SizeError == 0 ? new GlobalRead { Status = FetchStatus.Empty, Data = [] } : new GlobalRead { Status = FetchStatus.SizeFailed, Error = new Win32Error(f.SizeError, "偽のエラー") };
        var (partial, count) = CopyPlan.Global(size, limit, partialBytes);
        var data = f.Data is not null && f.Data.Length >= count ? f.Data[..count] : new byte[Math.Min(count, 16)];
        return new GlobalRead { Status = partial ? FetchStatus.SizeLimited : FetchStatus.Ok, Size = size, Data = data };
    }

    public BitmapHandleInfo ReadBitmap(nint handle, long pixelLimit) => ByHandle(handle).Bitmap ?? new BitmapHandleInfo { Width = 1, Height = 1, BitsPixel = 32, Planes = 1 };
    public int ReadPaletteEntries(nint handle) => 16;
    public EnhMetafileInfo ReadEnhMetafile(nint handle, long limit) => new() { Bytes = 100 };
    public MetafilePictInfo ReadMetafilePict(nint handle) => new();
    public WindowProcessInfo GetWindowProcess(nint hwnd) => WindowInfo ?? new WindowProcessInfo { Hwnd = hwnd };
    public (int Ansi, int Oem) GetSystemCodePages() => (1252, 437);
    public (int Ansi, int Oem)? GetLocaleCodePages(uint lcid) => LocaleCodePages.TryGetValue(lcid, out var v) ? v : null;
    public string ErrorMessage(int code) => $"説明{code}";
    public void Sleep(int milliseconds) => Sleeps++;
}
