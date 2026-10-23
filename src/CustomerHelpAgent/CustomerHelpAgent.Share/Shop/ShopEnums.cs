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
    Agent = 2,
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
