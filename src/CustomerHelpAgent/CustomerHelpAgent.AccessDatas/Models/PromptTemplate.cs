using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// AI 提示詞的一個版本（0.9.108 起）：管理員在「AI 提示詞」頁修改分析指示，每次儲存都新增一個版本，全部保留。
/// 同一個 <see cref="TemplateKey"/> 最多一個 <see cref="IsActive"/>；沒有任何作用中的版本時使用程式內建的預設。
/// 只存可修改的分析指示 —— 防注入與 Markdown 規則固定在程式裡，送出時自動接在最後，不存在這裡。
/// </summary>
public class PromptTemplate
{
    public int Id { get; set; }

    /// <summary><see cref="PromptTemplateKeys"/> 之一。</summary>
    [MaxLength(64)]
    public string TemplateKey { get; set; } = string.Empty;

    /// <summary>同一個範本內從 1 開始遞增；(TemplateKey, Version) 唯一。</summary>
    public int Version { get; set; }

    [MaxLength(8000)]
    public string Content { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    [MaxLength(200)]
    public string? Note { get; set; }

    [MaxLength(100)]
    public string? CreatedByAccount { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

public static class PromptTemplateKeys
{
    /// <summary>日誌檢視頁的 AI 分析。</summary>
    public const string LogAnalysis = "LogAnalysis";

    /// <summary>系統例外紀錄的 AI 例外分析（含追問）。</summary>
    public const string ExceptionAnalysis = "ExceptionAnalysis";

    /// <summary>顧客端「線上客服」售後 Agent 的系統指示（1.0.4 起）。</summary>
    public const string AfterSalesAgent = "AfterSalesAgent";

    /// <summary>客服後台 AI 初審：照片初審節點（M4，結構化輸出）。</summary>
    public const string AfterSalesPreReviewPhoto = "AfterSalesPreReviewPhoto";

    /// <summary>客服後台 AI 初審：歷史紀錄初審節點（M4，結構化輸出）。</summary>
    public const string AfterSalesPreReviewHistory = "AfterSalesPreReviewHistory";

    /// <summary>客服後台 AI 初審：摘要節點（M4，結構化輸出）。</summary>
    public const string AfterSalesPreReviewSummary = "AfterSalesPreReviewSummary";

    /// <summary>客服後台的 AI 助手（M4）。</summary>
    public const string AfterSalesStaffAssistant = "AfterSalesStaffAssistant";

    /// <summary>顧客端多 Agent（M5）：分流 Agent 的系統指示。</summary>
    public const string AfterSalesTriage = "AfterSalesTriage";

    /// <summary>顧客端多 Agent（M5）：換退貨專員的系統指示。</summary>
    public const string AfterSalesExchangeSpecialist = "AfterSalesExchangeSpecialist";

    /// <summary>顧客端多 Agent（M5）：品質專員的系統指示。</summary>
    public const string AfterSalesQualitySpecialist = "AfterSalesQualitySpecialist";

    /// <summary>顧客端多 Agent（M5）：物流專員的系統指示。</summary>
    public const string AfterSalesLogisticsSpecialist = "AfterSalesLogisticsSpecialist";

    public static readonly IReadOnlyList<string> All =
    [
        LogAnalysis,
        ExceptionAnalysis,
        AfterSalesAgent,
        AfterSalesPreReviewPhoto,
        AfterSalesPreReviewHistory,
        AfterSalesPreReviewSummary,
        AfterSalesStaffAssistant,
        AfterSalesTriage,
        AfterSalesExchangeSpecialist,
        AfterSalesQualitySpecialist,
        AfterSalesLogisticsSpecialist,
    ];
}
