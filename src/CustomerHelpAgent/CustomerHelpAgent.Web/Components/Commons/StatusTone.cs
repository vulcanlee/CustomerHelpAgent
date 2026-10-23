namespace CustomerHelpAgent.Web.Components.Commons;

/// <summary>
/// <see cref="StatusPill"/> 的語氣。呼叫端負責把自己的領域值對應到這裡的其中一種，
/// 元件本身不認識「啟用」「已完成」這類字串 —— 同一個字在不同模組語氣未必相同。
/// </summary>
public enum StatusTone
{
    /// <summary>中性。未知或不帶好壞的狀態（未開始、等待）。</summary>
    Neutral,

    /// <summary>正向。啟用、已完成。</summary>
    Positive,

    /// <summary>停用或關閉。刻意不是「負向」—— 停用不是錯誤。</summary>
    Muted,

    /// <summary>需要注意。暫緩、逾期。</summary>
    Warning,

    /// <summary>以品牌重點色強調。進行中、管理員這類「就是這一個」的標記。</summary>
    Accent,
}
