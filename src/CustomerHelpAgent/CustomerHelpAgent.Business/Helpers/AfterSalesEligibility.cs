using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Business.Helpers;

public sealed record EligibilityResult(bool IsEligible, IReadOnlyList<string> UnmetReasons, IReadOnlyList<string> MissingInfo);

/// <summary>售後資格判定（純函式，傳統流程與 M2 Agent 共用）。</summary>
public static class AfterSalesEligibility
{
    /// <summary>簽收後經過的天數（本地日曆日差）；未簽收回 null。</summary>
    public static int? DaysSinceDelivery(DateTime? deliveredAt, DateTime now)
        => deliveredAt is null ? null : (now.Date - deliveredAt.Value.Date).Days;

    /// <summary>出貨後超過此時間仍未簽收才可催件（剛好 3 天不算）。</summary>
    public static readonly TimeSpan UrgeAfter = TimeSpan.FromDays(3);

    /// <summary>未簽收，且出貨時間距今超過 <see cref="UrgeAfter"/>。</summary>
    public static bool CanRequestUrge(DateTime? deliveredAt, DateTime? shippedAt, DateTime now)
        => deliveredAt is null && shippedAt is { } s && now - s > UrgeAfter;

    public static EligibilityResult Evaluate(
        AfterSalesPolicyModel policy, DateTime? deliveredAt, bool? isWorn, bool hasPhoto, DateTime now,
        DateTime? shippedAt = null)
    {
        if (policy.Type == AfterSalesType.ShipmentUrge)
        {
            if (CanRequestUrge(deliveredAt, shippedAt, now))
            {
                return new EligibilityResult(true, [], []);
            }

            var reason = deliveredAt is not null ? "商品已簽收，不需催件"
                : shippedAt is null ? "訂單尚未出貨"
                : "出貨未滿 3 天，請再等候";
            return new EligibilityResult(false, [reason], []);
        }

        var unmet = new List<string>();
        var missing = new List<string>();

        var days = DaysSinceDelivery(deliveredAt, now);
        if (days is null)
        {
            unmet.Add("商品尚未簽收");
        }
        else if (deliveredAt > now)
        {
            unmet.Add("簽收日期異常，請洽客服");
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
