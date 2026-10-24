namespace CustomerHelpAgent.Share.Shop;

/// <summary>核准售後申請後建立的（模擬）處理單種類。</summary>
public enum FulfillmentKind
{
    ExchangeOrder = 1,
    RefundRecord = 2,
    ReplacementOrder = 3,
    ShipmentFollowUp = 4,
}

/// <summary>客服對售後申請的決定。</summary>
public enum StaffDecisionKind
{
    Approve = 1,
    Reject = 2,
    NeedMoreInfo = 3,
}
