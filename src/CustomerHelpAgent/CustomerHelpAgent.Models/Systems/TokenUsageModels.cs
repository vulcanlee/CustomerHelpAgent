using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Models.Systems;

/// <summary>
/// 一次 LLM 呼叫的用量，由呼叫端交給 TokenUsageLogService 記錄。
///
/// 放在 CustomerHelpAgent.Models 是因為生產端（Web 的 AI 服務）與消費端（Business 的服務）
/// 都要看得到它，而 Models 不相依任何其他專案，符合分層規範。
/// </summary>
public sealed class TokenUsageEntry
{
    public string Operation { get; init; } = string.Empty;

    public string CallKind { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string? Account { get; init; }

    public int? UserId { get; init; }

    public int? InputCount { get; init; }

    public int? OutputCount { get; init; }

    public int? TotalCount { get; init; }

    public int? CachedInputCount { get; init; }

    public int? ReasoningCount { get; init; }

    /// <summary>
    /// usage.prompt_tokens_details.image_tokens。圖片輸入<b>是 InputCount 的子集</b>，
    /// 不另外加總 —— 供應商把圖片 token 算在 prompt_tokens 裡面。定成加項會重複計費。
    /// </summary>
    public int? ImageInputCount { get; init; }

    /// <summary>圖片輸入中的快取命中，<b>是 ImageInputCount 與 CachedInputCount 的交集子集</b>。</summary>
    public int? ImageCachedInputCount { get; init; }

    /// <summary>圖片輸出 token，<b>是 OutputCount 的子集</b>，不另外加總。</summary>
    public int? ImageOutputCount { get; init; }

    public int? DurationSeconds { get; init; }

    /// <summary>
    /// 語音合成的計費字元數。與 token 無關的獨立計費單位，目前專案沒有這類呼叫。
    /// </summary>
    public int? CharacterCount { get; init; }

    public long ElapsedMilliseconds { get; init; }

    public bool Success { get; init; }

    public string? FailureReason { get; init; }

    /// <summary>
    /// 供應商回傳的原始 usage JSON（只有 usage 那一段）。會寫進檔案系統。
    ///
    /// ⚠️ <b>呼叫端絕不可把整個回應 body 塞進來</b> —— 那裡面有模型產生的內文，
    /// 而本專案的提示詞就是日誌內容。只取 usage 子物件（完整內容只進 AI 對話紀錄）。
    /// </summary>
    public string? RawUsageJson { get; init; }

    public DateTime OccurredAt { get; init; } = DateTime.Now;

    /// <summary>
    /// 本次呼叫的識別碼，與 AI 對話紀錄（AiCallLog.CallId）相同，用來從用量明細找到完整對話。
    /// 0.9.72 之前的紀錄為 null。
    /// </summary>
    public Guid? CallId { get; init; }
}

/// <summary>
/// 作業名稱常數。新增 LLM 呼叫點時在此登記一個，頁面的「依作業」統計就會自動長出來。
/// </summary>
public static class TokenUsageOperations
{
    public const string AiLogAnalysis = "AI 日誌分析";

    /// <summary>
    /// 系統健康監控頁的 LLM 連線探測（送一句 hello 確認 API 可用）。
    ///
    /// ⚠️ 每開一次 /system-health 就會產生一筆。次數會比 AI 日誌分析多得多，
    /// 但每筆只有幾十個 token。要單獨檢視請用頁面的「作業」篩選器。
    /// </summary>
    public const string SystemHealthCheck = "系統健康檢測";

    /// <summary>
    /// 系統例外紀錄明細窗的 AI 例外分析（0.9.68 起）。初始分析與每一輪追問都記在這個名稱下；
    /// 追問會帶上前文，所以輪數越多、單筆輸入量越大。
    /// </summary>
    public const string AiExceptionAnalysis = "AI 例外分析";
}

/// <summary>
/// API 型別常數。目前只有 Chat；日後若接 embedding、語音轉文字等再於此登記。
/// </summary>
public static class TokenUsageCallKinds
{
    public const string Chat = "Chat";
}

/// <summary>Token 用量的查詢條件。比照 ExceptionLogQuery，不污染共用的 DataRequest。</summary>
public class TokenUsageQuery
{
    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    /// <summary>帳號，部分比對。</summary>
    public string Account { get; set; } = string.Empty;

    public string Operation { get; set; } = string.Empty;

    public string CallKind { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public int CurrentPage { get; set; } = 1;

    public int PageSize { get; set; } = MagicObjectHelper.PageSize;

    public string SortField { get; set; } = string.Empty;

    public bool? SortDescending { get; set; }
}

/// <summary>
/// 篩選範圍的合計。
///
/// 注意加總語意：<b>快取是輸入的折扣子集、推理計入輸出</b>，兩者都只是「其中多少」，
/// 不再另外加進合計，否則會重複計算。
/// </summary>
public sealed class TokenUsageSummary
{
    public long InputCount { get; set; }

    public long OutputCount { get; set; }

    public long CachedInputCount { get; set; }

    public long ReasoningCount { get; set; }

    public long TotalCount { get; set; }

    /// <summary>估算費用（美金）。未定價的列不計入。</summary>
    public double CostUsd { get; set; }

    /// <summary>
    /// 估算費用（台幣）。<b>逐列加總而來</b>，不是由 CostUsd 乘上某個匯率換算的。
    /// 篩選區間橫跨匯率調整時，這兩個數字彼此推不出來，那是正確的。
    /// </summary>
    public double CostTwd { get; set; }

    /// <summary>未定價（算不出費用）的呼叫次數，不含在上面兩個金額裡。</summary>
    public int UnpricedCount { get; set; }

    /// <summary>納入統計的呼叫次數。</summary>
    public int CallCount { get; set; }
}

/// <summary>分組統計的一列（依使用者／作業／模型／型別共用）。</summary>
public sealed class TokenUsageGroupRow
{
    public string Key { get; set; } = string.Empty;

    public long InputCount { get; set; }

    public long OutputCount { get; set; }

    public long CachedInputCount { get; set; }

    public long ReasoningCount { get; set; }

    public long TotalCount { get; set; }

    /// <summary>估算費用（美金）。未定價的列不計入。</summary>
    public double CostUsd { get; set; }

    /// <summary>
    /// 估算費用（台幣）。<b>逐列加總而來</b>，不是由 CostUsd 乘上某個匯率換算的。
    /// 篩選區間橫跨匯率調整時，這兩個數字彼此推不出來，那是正確的。
    /// </summary>
    public double CostTwd { get; set; }

    /// <summary>未定價（算不出費用）的呼叫次數，不含在上面兩個金額裡。</summary>
    public int UnpricedCount { get; set; }

    public int CallCount { get; set; }
}

/// <summary>分組統計的維度。</summary>
public enum TokenUsageGroupBy
{
    Account,
    Operation,
    Model,
    CallKind,
}

/// <summary>三個下拉選單的可選值，取自資料庫中實際出現過的值。</summary>
public sealed class TokenUsageFilterOptions
{
    public IReadOnlyList<string> Operations { get; set; } = [];

    public IReadOnlyList<string> CallKinds { get; set; } = [];

    public IReadOnlyList<string> Models { get; set; } = [];
}

/// <summary>
/// 逐日統計的一列。「最近 N 天」的三張卡與「每日趨勢」頁籤共用這個形狀。
///
/// 注意加總語意與 <see cref="TokenUsageGroupRow"/> 一致：快取是輸入的折扣子集、
/// 推理計入輸出，因此這裡只留下可以直接畫在時間軸上的合計與費用。
/// </summary>
public sealed class TokenUsageDailyRow
{
    /// <summary>該日的零時（以 OccurredAt 的當地日期分組）。</summary>
    public DateTime Date { get; set; }

    public long TotalCount { get; set; }

    /// <summary>估算費用（美金）。未定價的列不計入。</summary>
    public double CostUsd { get; set; }

    /// <summary>
    /// 估算費用（台幣）。<b>逐列加總而來</b>，不是由 CostUsd 乘上某個匯率換算的。
    /// </summary>
    public double CostTwd { get; set; }

    /// <summary>未定價（算不出費用）的呼叫次數，不含在上面兩個金額裡。</summary>
    public int UnpricedCount { get; set; }

    public int CallCount { get; set; }
}

/// <summary>
/// 「最近 N 天」的<b>唯一定義</b>：含今天往前數 N 日。
/// 近 1 天＝今天；近 7 天＝今天往前數 7 日（也就是 today-6 到 today）。
///
/// ⚠️ 摘要卡與「每日趨勢」的預設區間都必須走這裡。兩邊各自寫一次日期運算，
/// 遲早會出現「卡片說近 7 天、趨勢畫了 8 格」這種對不起來的狀況。
/// </summary>
public static class TokenUsageRanges
{
    public const int RecentDay = 1;

    public const int RecentWeek = 7;

    public const int RecentMonth = 30;

    /// <summary>「近 <paramref name="days"/> 天」的起訖日（皆為日期，含頭含尾）。</summary>
    public static (DateTime Start, DateTime End) Recent(DateTime today, int days)
        => (today.Date.AddDays(-(days - 1)), today.Date);
}
