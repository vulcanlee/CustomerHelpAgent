using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Models.AdapterModel;

public class ProjectAdapterModel : ICloneable, IValidatableObject
{
    /// <summary>軟刪除狀態（0.9.94 起，只由實體對應過來，供「顯示已刪除」清單使用）。</summary>
    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public string? DeletedBy { get; set; }

    /// <summary>樂觀並行的版本號：開啟編輯時的值，存檔時用來比對資料是否已被別人修改（0.9.93 起）。</summary>
    public string ConcurrencyStamp { get; set; } = string.Empty;

    public static readonly IReadOnlyList<string> StatusOptions =
    [
        "未開始",
        "進行中",
        "已完成",
        "暫緩",
        "等待"
    ];

    public static readonly IReadOnlyList<string> PriorityOptions =
    [
        "低",
        "中",
        "高"
    ];

    public int Id { get; set; }

    [Required(ErrorMessage = "專案標題 不可為空白")]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    [Required(ErrorMessage = "狀態 不可為空白")]
    public string Status { get; set; } = StatusOptions[0];

    [Required(ErrorMessage = "優先級 不可為空白")]
    public string Priority { get; set; } = PriorityOptions[1];

    [Range(0, 100, ErrorMessage = "完成百分比 必須介於 0 到 100")]
    public int CompletionPercentage { get; set; }

    [Required(ErrorMessage = "負責人 不可為空白")]
    public string Owner { get; set; } = string.Empty;

    public List<string> Categories { get; set; } = [];

    public List<string> Teams { get; set; } = [];

    public string CategoriesText => string.Join("、", Categories);

    public string TeamsText => string.Join("、", Teams);

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public List<ProjectFileAdapterModel> Files { get; set; } = [];

    public ProjectAdapterModel Clone()
    {
        return ((ICloneable)this).Clone() as ProjectAdapterModel ?? new ProjectAdapterModel();
    }

    object ICloneable.Clone()
    {
        return MemberwiseClone();
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StartDate.HasValue && EndDate.HasValue && EndDate.Value < StartDate.Value)
        {
            yield return new ValidationResult("結束日期不可早於開始日期。", [nameof(EndDate)]);
        }
    }
}
