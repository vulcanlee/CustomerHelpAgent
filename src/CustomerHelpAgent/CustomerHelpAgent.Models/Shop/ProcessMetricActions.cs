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

    // 顧客（AI 售後客服 Agent）
    public const string AgentSendMessage = "AgentSendMessage";
    public const string AgentConfirm = "AgentConfirm";
    public const string AgentReject = "AgentReject";
    public const string AgentUploadPhoto = "AgentUploadPhoto";

    /// <summary>
    /// 確認卡確實建立了售後申請（M6）：在 <c>ConfirmAsync</c> 之後、只在有申請編號時記一筆（帶申請 Id，記憶卡不記）。
    /// 不是顧客的點擊，不算操作步數；效益對照的申請筆數靠它（<see cref="AgentConfirm"/> 是點擊，可能是記憶卡或沒建成）。
    /// </summary>
    public const string AgentRequestCreated = "AgentRequestCreated";

    // 客服
    public const string OpenRequest = "OpenRequest";
    public const string ViewOrder = "ViewOrder";
    public const string ViewPolicy = "ViewPolicy";
    public const string RequestMoreInfo = "RequestMoreInfo";
    public const string Approve = "Approve";
    public const string Reject = "Reject";
}
