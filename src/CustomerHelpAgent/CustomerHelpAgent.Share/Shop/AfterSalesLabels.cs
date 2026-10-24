namespace CustomerHelpAgent.Share.Shop;

/// <summary>售後申請類型與狀態的中文標籤（顧客端與後台共用，避免各頁各寫一份而漂移）。</summary>
public static class AfterSalesLabels
{
    public static string TypeText(AfterSalesType type) => type switch
    {
        AfterSalesType.SizeExchange => "換尺碼",
        AfterSalesType.ColorExchange => "換顏色",
        AfterSalesType.ReturnRefund => "退貨退款",
        AfterSalesType.QualityIssue => "品質問題（開膠等）",
        AfterSalesType.ShipmentUrge => "物流催件",
        _ => type.ToString(),
    };

    public static string StatusText(AfterSalesStatus status) => status switch
    {
        AfterSalesStatus.Submitted => "已提交",
        AfterSalesStatus.NeedMoreInfo => "待補件",
        AfterSalesStatus.Approved => "已核准",
        AfterSalesStatus.Rejected => "已駁回",
        _ => status.ToString(),
    };

    public static string ChannelText(AfterSalesChannel channel) => channel switch
    {
        AfterSalesChannel.Traditional => "傳統表單",
        AfterSalesChannel.Agent => "AI 助理",
        AfterSalesChannel.AgentHandoff => "AI 多 Agent",
        _ => channel.ToString(),
    };
}
