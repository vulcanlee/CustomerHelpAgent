namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// LLM API 的 token 用量紀錄：一次呼叫一列。
///
/// 與其他兩張紀錄表的分工：
/// <list type="bullet">
///   <item><see cref="AuditLog"/> 記「誰對什麼做了什麼」的業務稽核軌跡。</item>
///   <item><see cref="ExceptionLog"/> 記「程式在哪裡拋出了什麼例外」。</item>
///   <item>本表記「哪個作業、用哪個模型、花了多少 token」。</item>
/// </list>
/// 三者刻意不合併。
///
/// ⚠️ <b>供應商回傳的原始 usage JSON 不存在本表</b>，而是寫到檔案系統
/// （見 <see cref="RawUsageFile"/>），本表只保留可聚合統計的數值欄位。
///
/// ⚠️ <b>本表絕不儲存提示詞與模型回應內文。</b>AI 日誌分析的提示詞就是整份日誌內容，
/// 例外分析則含堆疊與使用者帳號。完整內容只存在管理員專屬的 <see cref="AiCallLog"/>
/// （內容檔在 AiCallLogPath，依 RetentionDays 自動過期），兩者以 <see cref="CallId"/> 關聯。
/// </summary>
public class TokenUsageLog
{
    public int Id { get; set; }

    public DateTime OccurredAt { get; set; }

    /// <summary>作業（是哪個功能在呼叫），例如「AI 日誌分析」。</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>API 型別，例如 Chat；日後可能有 Embedding、Transcription 等。</summary>
    public string CallKind { get; set; } = string.Empty;

    /// <summary>服務供應商，例如 AzureOpenAI、OpenAI。</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>實際使用的模型（優先取回應的 model，空的才退回設定值）。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>觸發呼叫的使用者帳號。系統自動觸發時為 null。刻意不記姓名／Email。</summary>
    public string? Account { get; set; }

    public int? UserId { get; set; }

    /// <summary>usage.prompt_tokens（輸入／送出）。</summary>
    public int? InputCount { get; set; }

    /// <summary>usage.completion_tokens（輸出／接收）。</summary>
    public int? OutputCount { get; set; }

    /// <summary>usage.total_tokens。</summary>
    public int? TotalCount { get; set; }

    /// <summary>usage.prompt_tokens_details.cached_tokens。快取是<b>輸入的折扣子集</b>，不另外加總。</summary>
    public int? CachedInputCount { get; set; }

    /// <summary>usage.completion_tokens_details.reasoning_tokens。推理<b>計入輸出</b>，不另外加總。</summary>
    public int? ReasoningCount { get; set; }

    /// <summary>
    /// usage.prompt_tokens_details.image_tokens。圖片輸入<b>是 <see cref="InputCount"/> 的子集</b>，
    /// 不另外加總。
    ///
    /// ⚠️ 這個「子集」定義是刻意的，不可改成加項：供應商把圖片 token 算在 prompt_tokens 裡面，
    /// 若定成額外加項，第一個照抄供應商語意填值的人就會讓每筆圖片呼叫<b>重複計費</b>，
    /// 而費用已經是寫死的快照，回頭沒得修。
    ///
    /// 目前專案沒有圖片類呼叫，欄位先預留。
    /// </summary>
    public int? ImageInputCount { get; set; }

    /// <summary>圖片輸入中的快取命中，<b>是 <see cref="ImageInputCount"/> 與 <see cref="CachedInputCount"/> 的交集子集</b>。</summary>
    public int? ImageCachedInputCount { get; set; }

    /// <summary>圖片輸出 token，<b>是 <see cref="OutputCount"/> 的子集</b>，不另外加總。</summary>
    public int? ImageOutputCount { get; set; }

    /// <summary>
    /// 音訊時長（秒）。部分語音模型依時長計費、不回傳 token 數。
    /// 目前專案沒有這類呼叫，欄位先預留，這類列不計入 token 合計。
    /// </summary>
    public int? DurationSeconds { get; set; }

    /// <summary>
    /// 語音合成的計費字元數。與 token 無關的獨立計費單位。
    /// 目前專案沒有這類呼叫，欄位先預留。
    /// </summary>
    public int? CharacterCount { get; set; }

    /// <summary>本次呼叫耗時。</summary>
    public long ElapsedMilliseconds { get; set; }

    /// <summary>是否成功取得可用內容。失敗也要入帳，見 <see cref="FailureReason"/>。</summary>
    public bool Success { get; set; }

    /// <summary>
    /// 失敗原因（對應 AiAnalysisFailureReason）。
    /// 其中「回應成功但內容為空」特別值得注意：付了錢卻沒拿到東西，而且 API 有回傳用量。
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// 估算費用（美金）。<b>null 就是「未定價」</b> —— 找不到該模型的費率設定，或匯率未設定。
    /// 有比到費率但這次用量是 0 時存 0，兩者意義不同。
    ///
    /// 型別是 double 而非 decimal：SQLite 把 decimal 存成 TEXT，無法在 SQL 端可靠彙總，
    /// 而「篩選範圍的總花費」正是這個功能的核心。帳目可重現性不靠這個數字，
    /// 而靠下面的單價與匯率快照 —— 有快照與 token 數，任何時候都能重算驗證。
    /// </summary>
    public double? CostUsd { get; set; }

    /// <summary>估算費用（台幣）＝ <see cref="CostUsd"/> × <see cref="CostExchangeRate"/>。</summary>
    public double? CostTwd { get; set; }

    /// <summary>計算當下的美金兌台幣匯率快照。日後調整設定不影響這一列。</summary>
    public double? CostExchangeRate { get; set; }

    /// <summary>
    /// 實際套用的費率設定鍵。與 <see cref="Model"/> 不同時，代表是前綴比對來的
    /// （例如回應的 gpt-4o-2024-08-06 套用了 gpt-4o 的費率）。
    /// </summary>
    public string? CostPriceKey { get; set; }

    /// <summary>本次是否套用長脈絡費率。</summary>
    public bool CostLongContext { get; set; }

    /// <summary>
    /// 生效費率組的精簡 JSON（只含非零項），例如 {"TextInput":4,"TextCachedInput":0.4,"TextOutput":20}。
    ///
    /// ⚠️ 這是本表「只留可聚合的數值欄位」原則的<b>刻意例外</b>。它是設定衍生資料，
    /// 不含任何使用者輸入或模型輸出，所以不踩上面那條安全紅線。
    /// 不能改寫進 <see cref="RawUsageFile"/> 那個檔：那個檔可能是 null，
    /// 而且會被「清除此日之前」獨立刪掉，帳就沒了。
    /// </summary>
    public string? CostRateSnapshot { get; set; }

    /// <summary>原始 usage JSON 的相對路徑，例如 202609/ab12….json。寫檔失敗時為 null。</summary>
    public string? RawUsageFile { get; set; }

    /// <summary>呼叫識別碼，對應 <see cref="AiCallLog.CallId"/>（0.9.72 起；之前的紀錄為 null）。</summary>
    public Guid? CallId { get; set; }
}
