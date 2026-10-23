using CustomerHelpAgent.Models.Shop;

namespace CustomerHelpAgent.Business.Helpers;

public sealed record EligibilityResult(bool IsEligible, IReadOnlyList<string> UnmetReasons, IReadOnlyList<string> MissingInfo);

/// <summary>售後資格判定（純函式，傳統流程與 M2 Agent 共用）。</summary>
public static class AfterSalesEligibility
{
    /// <summary>簽收後經過的天數（本地日曆日差）；未簽收回 null。</summary>
    public static int? DaysSinceDelivery(DateTime? deliveredAt, DateTime now)
        => deliveredAt is null ? null : (now.Date - deliveredAt.Value.Date).Days;

    public static EligibilityResult Evaluate(
        AfterSalesPolicyModel policy, DateTime? deliveredAt, bool? isWorn, bool hasPhoto, DateTime now)
    {
        var unmet = new List<string>();
        var missing = new List<string>();

        var days = DaysSinceDelivery(deliveredAt, now);
        if (days is null)
        {
            unmet.Add("商品尚未簽收");
        }
        else if (policy.WindowDays is { } window && days > window)
        {
            unmet.Add($"已超過 {window} 天申請期限（簽收後第 {days} 天）");
        }

        if (policy.RequiresUnworn)
        {
            if (isWorn == true)
            {
                unmet.Add("商品已穿過，不符合未穿著條件");
            }
            else if (isWorn is null)
            {
                missing.Add("是否穿過");
            }
        }

        if (policy.RequiresPhoto && !hasPhoto)
        {
            missing.Add("照片");
        }

        return new EligibilityResult(unmet.Count == 0 && missing.Count == 0, unmet, missing);
    }
}
