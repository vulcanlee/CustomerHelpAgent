using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Models.Shop;

/// <summary>售後政策的畫面／服務模型（版本化，不就地編輯）。</summary>
public class AfterSalesPolicyModel : ICloneable
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public AfterSalesType Type { get; set; }
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

    /// <summary>
    /// 新增版本時的基準：開窗預填來源的 <see cref="Version"/>。與目前生效版本不同代表別人已更新，服務拒絕新增；
    /// null（全新的規則代碼，沒有現行版本可預填）時不檢查。
    /// </summary>
    public int? BaseVersion { get; set; }

    public AfterSalesPolicyModel Clone()
    {
        return (AfterSalesPolicyModel)((ICloneable)this).Clone();
    }

    object ICloneable.Clone()
    {
        return MemberwiseClone();
    }
}

public enum PolicyLookupStatus
{
    Found,
    NoPolicy,
    Conflict,
}

public sealed record PolicyLookupResult(PolicyLookupStatus Status, IReadOnlyList<AfterSalesPolicyModel> Policies);
