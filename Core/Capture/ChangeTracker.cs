namespace ClipInspect.Core.Capture;

public enum ChangeState { NotCaptured, Unchanged, ChangedByClipInspect, ChangedByOther, ChangedByOtherThenClipInspect }

/// <summary>
/// 取得後の変更の状態（§27・§34 D9）。タイマーで監視せず、画面のアクティブ化と操作のたびに番号を渡して更新する。
/// </summary>
public sealed class ChangeTracker
{
    private uint? _baseline;
    private uint? _ownWrite;
    private bool _otherObserved;

    public ChangeState State { get; private set; } = ChangeState.NotCaptured;

    public static string Label(ChangeState s) => s switch
    {
        ChangeState.NotCaptured => "未取得",
        ChangeState.Unchanged => "変更なし",
        ChangeState.ChangedByClipInspect => "取得後に変更（ClipInspectのコピーによる）",
        ChangeState.ChangedByOther => "取得後に変更（他のアプリ等）",
        _ => "取得後に変更（他のアプリ等による変更あり・その後 ClipInspect のコピー）",
    };

    /// <summary>新しい取得の後に呼ぶ。</summary>
    public void Reset(uint baseline)
    {
        _baseline = baseline;
        _ownWrite = null;
        _otherObserved = false;
        State = ChangeState.Unchanged;
    }

    /// <summary>詳細取得の後など、基準の番号が進んだとき（自分の読み取りによる変化）。</summary>
    public void MoveBaseline(uint baseline)
    {
        if (State == ChangeState.Unchanged) _baseline = baseline;
    }

    public ChangeState Observe(uint current)
    {
        if (_baseline is null) return State = ChangeState.NotCaptured;
        if (current == _baseline && _ownWrite is null && !_otherObserved) return State = ChangeState.Unchanged;
        if (_ownWrite is { } own && current == own)
            return State = _otherObserved ? ChangeState.ChangedByOtherThenClipInspect : ChangeState.ChangedByClipInspect;
        if (current != _baseline) _otherObserved = true;
        _ownWrite = null;
        return State = _otherObserved ? ChangeState.ChangedByOther : ChangeState.Unchanged;
    }

    /// <summary>書き込みの直前に呼ぶ（他のアプリ等による変更を記録してから書く）。</summary>
    public void BeforeWrite(uint current) => Observe(current);

    /// <summary>書き込みが確認できたとき、その番号を記録する。</summary>
    public void AfterWrite(uint writtenSeq)
    {
        if (_baseline is null) return;
        _ownWrite = writtenSeq;
        Observe(writtenSeq);
    }
}
