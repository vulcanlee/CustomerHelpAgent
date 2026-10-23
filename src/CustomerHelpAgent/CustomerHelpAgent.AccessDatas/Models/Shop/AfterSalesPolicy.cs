using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>售後政策（以新增版本取代就地編輯）。</summary>
public class AfterSalesPolicy
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public AfterSalesType Type { get; set; }

    /// <summary>適用類別，值見 <see cref="ProductCategories"/>。</summary>
    public string Category { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public int? WindowDays { get; set; }
    public bool RequiresUnworn { get; set; }
    public bool RequiresPhoto { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? CreatedBy { get; set; }
}
