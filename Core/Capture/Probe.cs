using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ClipInspect.Core.Analysis;
using ClipInspect.Core.Model;
using ClipInspect.Core.Native;

namespace ClipInspect.Core.Capture;

/// <summary>
/// OLE・.NET の形式の確認を専用の STA スレッドで行い、時間で打ち切る（§34 D2）。
/// 打ち切ったスレッドは背景スレッドのまま残す（コピー元が応答しない場合に止められないため）。
/// </summary>
public static class ProbeRunner
{
    public static ProbeResult Run(Func<ProbeResult> probe, TimeSpan timeout)
    {
        ProbeResult? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = probe(); }
            catch (Exception ex) { error = ex; }
        })
        { IsBackground = true, Name = "ClipInspect probe" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(timeout))
        {
            return new ProbeResult { OleStatus = ProbeStatus.Timeout, NetStatus = ProbeStatus.Timeout };
        }
        if (result is not null) return result;
        var msg = error?.Message ?? "結果がありません";
        return new ProbeResult { OleStatus = ProbeStatus.Failed, OleError = msg, NetStatus = ProbeStatus.Failed, NetError = msg };
    }
}

/// <summary>OLE の IDataObject から見える形式（データは読まない）。STA スレッドで呼ぶ。</summary>
public static class OleProbe
{
    public static void Fill(ProbeResult r)
    {
        var init = NativeMethods.OleInitialize(0);
        try
        {
            var hr = NativeMethods.OleGetClipboard(out var data);
            if (hr != 0)
            {
                r.OleStatus = ProbeStatus.Failed;
                r.OleError = $"OleGetClipboard が 0x{hr:X8} を返しました";
                return;
            }
            try
            {
                var e = data.EnumFormatEtc(DATADIR.DATADIR_GET);
                var buf = new FORMATETC[1];
                var fetched = new int[1];
                var count = 0;
                while (e.Next(1, buf, fetched) == 0 && fetched[0] == 1 && count++ < Limits.EnumFormats)
                {
                    var id = (uint)(ushort)buf[0].cfFormat;
                    var name = FormatCatalog.NameOf(id, id >= 0xC000 ? new Win32ClipboardApi().GetFormatName(id) : null).Name;
                    r.Ole.Add(new ProbeItem { Id = id, Name = name, Tymed = TymedText(buf[0].tymed) });
                    if (buf[0].ptd != 0) Marshal.FreeCoTaskMem(buf[0].ptd);
                }
                r.OleStatus = ProbeStatus.Ok;
            }
            finally
            {
                Marshal.ReleaseComObject(data);
            }
        }
        catch (Exception ex)
        {
            r.OleStatus = ProbeStatus.Failed;
            r.OleError = ex.Message;
        }
        finally
        {
            if (init >= 0) NativeMethods.OleUninitialize();
        }
    }

    private static string TymedText(TYMED t)
    {
        var parts = new List<string>();
        foreach (TYMED v in Enum.GetValues<TYMED>())
        {
            if (v != TYMED.TYMED_NULL && t.HasFlag(v)) parts.Add(v.ToString());
        }
        return parts.Count == 0 ? "TYMED_NULL" : string.Join("|", parts);
    }
}
