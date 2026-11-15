using System.Text;
using ClipInspect.Core.Analysis;
using ClipInspect.Core.Model;
using ClipInspect.Core.Native;

namespace ClipInspect.Core.Capture;

public enum WriteStatus { Written, WrittenButReplaced, Busy, ProtectionFailed, Failed }

public sealed class WriteResult
{
    public WriteStatus Status { get; init; }
    public uint? Sequence { get; init; }
    public Win32Error? Error { get; init; }

    public string Message => Status switch
    {
        WriteStatus.Written or WriteStatus.WrittenButReplaced => "コピーしました。",
        WriteStatus.Busy => "コピーできませんでした（他のアプリが使用中）。",
        WriteStatus.ProtectionFailed => "コピーできませんでした（履歴・同期の抑止を付けられないため、内容も置きませんでした）。" + (Error is null ? "" : $"（{Error}）"),
        _ => "コピーできませんでした。" + (Error is null ? "" : $"（{Error}）"),
    };
}

/// <summary>
/// ClipInspect からの書き込み（§29）。先に保護指定（CanIncludeInClipboardHistory = 0・CanUploadToCloudClipboard = 0）を置き、
/// 置けなければ本体も置かずに失敗にする。ExcludeClipboardContentFromMonitorProcessing は付けない。
/// </summary>
public sealed class ClipboardWriter(IClipboardWriterApi api)
{
    public const string PngFormatName = "PNG";

    public async Task<WriteResult> WriteAsync(nint owner, IReadOnlyList<(uint Format, byte[] Data)> items, Func<int, Task>? delay = null)
    {
        delay ??= ms => Task.Delay(ms);
        var history = api.RegisterFormat(DwordAnalyzer.CanIncludeHistory);
        var cloud = api.RegisterFormat(DwordAnalyzer.CanUploadCloud);
        if (history == 0 || cloud == 0) return new WriteResult { Status = WriteStatus.ProtectionFailed };
        var zero = new byte[4];
        for (var attempt = 0; attempt < Limits.OpenRetryCount; attempt++)
        {
            if (attempt > 0) await delay(Limits.OpenRetryDelayMs);
            if (!api.Open(owner, out _)) continue;
            var status = WriteStatus.Written;
            Win32Error? error = null;
            try
            {
                if (!api.Empty(out var e0)) { status = WriteStatus.Failed; error = Err(e0); }
                else if (!api.SetData(history, zero, out var e1) || !api.SetData(cloud, zero, out e1))
                {
                    status = WriteStatus.ProtectionFailed;
                    error = Err(e1);
                }
                else
                {
                    foreach (var (format, data) in items)
                    {
                        if (!api.SetData(format, data, out var e2)) { status = WriteStatus.Failed; error = Err(e2); break; }
                    }
                }
                // 途中で失敗したら、閉じる前に空にする（保護指定のない本文や一部だけの内容を他のアプリに渡さない）。
                if (status != WriteStatus.Written) api.Empty(out _);
            }
            finally
            {
                api.Close();
            }
            if (status != WriteStatus.Written) return new WriteResult { Status = status, Error = error };
            // 閉じると Windows が変換形式を加えて番号が進むため、閉じた後に「番号→所有者→番号」で自分の書き込みを確かめる。
            var before = api.GetSequenceNumber();
            var ownerNow = api.GetOwner();
            var after = api.GetSequenceNumber();
            return ownerNow == owner && before == after
                ? new WriteResult { Status = WriteStatus.Written, Sequence = after }
                : new WriteResult { Status = WriteStatus.WrittenButReplaced };
        }
        return new WriteResult { Status = WriteStatus.Busy };
    }

    /// <summary>CF_UNICODETEXT のバイト列（UTF-16LE＋NUL）。</summary>
    public static byte[] UnicodeText(string text)
    {
        var bytes = new byte[(text.Length + 1) * 2];
        Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }

    private Win32Error Err(int code) => new(code, api.ErrorMessage(code));
}

/// <summary>IClipboardWriterApi の Win32 実装。</summary>
public sealed class Win32ClipboardWriterApi : IClipboardWriterApi
{
    public uint GetSequenceNumber() => NativeMethods.GetClipboardSequenceNumber();

    public bool Open(nint owner, out int error)
    {
        var ok = NativeMethods.OpenClipboard(owner);
        error = ok ? 0 : System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        return ok;
    }

    public bool Empty(out int error)
    {
        var ok = NativeMethods.EmptyClipboard();
        error = ok ? 0 : System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        return ok;
    }

    public bool SetData(uint format, ReadOnlySpan<byte> data, out int error)
    {
        var h = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (nuint)Math.Max(1, data.Length));
        if (h == 0) { error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError(); return false; }
        var p = NativeMethods.GlobalLock(h);
        if (p == 0)
        {
            error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            NativeMethods.GlobalFree(h);
            return false;
        }
        unsafe
        {
            data.CopyTo(new Span<byte>((void*)p, data.Length));
        }
        NativeMethods.GlobalUnlock(h);
        if (NativeMethods.SetClipboardData(format, h) != 0) { error = 0; return true; } // 以後の解放はシステムが行う
        error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        NativeMethods.GlobalFree(h);
        return false;
    }

    public void Close() => NativeMethods.CloseClipboard();
    public nint GetOwner() => NativeMethods.GetClipboardOwner();
    public uint RegisterFormat(string name) => NativeMethods.RegisterClipboardFormatW(name);
    public string ErrorMessage(int code) => new System.ComponentModel.Win32Exception(code).Message;
}
