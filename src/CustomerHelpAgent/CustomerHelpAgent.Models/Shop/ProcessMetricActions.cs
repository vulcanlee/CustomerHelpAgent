namespace CustomerHelpAgent.Models.Shop;

/// <summary>流程埋點的動作代碼（<c>ProcessMetric.Action</c>，長度 ≤ 64）。</summary>
public static class ProcessMetricActions
{
    // 顧客
    public const string OpenForm = "OpenForm";
    public const string SelectOrderItem = "SelectOrderItem";
    public const string SelectType = "SelectType";
    public const string FillDetail = "FillDetail";
    public const string UploadPhoto = "UploadPhoto";
    public const string Submit = "Submit";
    public const string ViewRequests = "ViewRequests";
    public const string Resubmit = "Resubmit";
    public const string ViewOrders = "ViewOrders";
    public const string ViewShipment = "ViewShipment";
    public const string UrgeShipment = "UrgeShipment";

    // 客服
    public const string OpenRequest = "OpenRequest";
    public const string ViewOrder = "ViewOrder";
    public const string ViewPolicy = "ViewPolicy";
    public const string RequestMoreInfo = "RequestMoreInfo";
    public const string Approve = "Approve";
    public const string Reject = "Reject";
}
