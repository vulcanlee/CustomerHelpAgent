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

    public static readonly IReadOnlyList<string> All = [LogAnalysis, ExceptionAnalysis];
}
