namespace CustomerHelpAgent.Share.Shop;

/// <summary>售後申請類型。</summary>
public enum AfterSalesType
{
    SizeExchange = 1,
    ColorExchange = 2,
    ReturnRefund = 3,
    QualityIssue = 4,
    ShipmentUrge = 5,
}

/// <summary>售後申請狀態。</summary>
public enum AfterSalesStatus
{
    Submitted = 1,
    NeedMoreInfo = 2,
    Approved = 3,
    Rejected = 4,
}

/// <summary>申請管道。</summary>
public enum AfterSalesChannel
{
    Traditional = 1,

    /// <summary>單一售後 Agent（「AI 助理」）。</summary>
    Agent = 2,

    /// <summary>多 Agent 交接模式（M5，「AI 多 Agent」）；與 <see cref="Agent"/> 同屬 AI 管道（<c>AfterSalesChannelExtensions.IsAi</c>）。</summary>
    AgentHandoff = 3,
}

/// <summary>流程操作者。</summary>
public enum ProcessActor
{
    Customer = 1,
    Staff = 2,
}

/// <summary>商品與政策適用的類別名稱。</summary>
public static class ProductCategories
{
    public const string Shoes = "鞋類";
    public const string Apparel = "服飾";
    public const string All = "全部";
}
