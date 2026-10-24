namespace CustomerHelpAgent.Share.Demo;

/// <summary>
/// 展示用的故障模擬（只在 <c>DemoSettings:Enabled</c> 時生效）。注入模擬業務服務，Agent 與傳統流程同受影響；
/// 只有 <see cref="PermissionExpired"/> 只影響 Agent 工具。資料庫以名稱（不是數值）保存，新增項目請加在最後、不要改名。
/// </summary>
public enum DemoFault
{
    /// <summary>訂單系統逾時：訂單讀取先等待 <c>FaultDelay</c> 再拋 <see cref="TimeoutException"/>。</summary>
    OrderServiceTimeout,

    /// <summary>訂單查無：我的訂單回空清單、單筆訂單與訂單商品回 null。</summary>
    OrdersNotFound,

    /// <summary>物流異常：訂單明細的物流狀態改為「配送異常」並附一筆異常事件（只改讀出的結果）。</summary>
    ShipmentAnomaly,

    /// <summary>規則服務停擺：查詢生效規則拋 <see cref="InvalidOperationException"/>。</summary>
    PolicyServiceUnavailable,

    /// <summary>規則衝突：查詢生效規則回「衝突」（現行版本＋一筆不落地的合成版本）。</summary>
    PolicyConflict,

    /// <summary>無適用規則：查詢生效規則回「查無」。</summary>
    PolicyNotFound,

    /// <summary>權限過期：Agent 工具的身分檢查一律不通過（回 forbidden）；傳統流程不模擬。</summary>
    PermissionExpired,
}

/// <summary>展示模式：即時（呼叫真實模型）或回放（重播錄製的模型回應）。</summary>
public enum DemoMode
{
    Live,
    Replay,
}

/// <summary>錄製的顧客動作（1.0.5 起）。資料庫以數值保存，新增項目請加在最後、不要調整順序。</summary>
public enum AgentRecordedTurnKind
{
    /// <summary>顧客送出一句話。</summary>
    Send,

    /// <summary>顧客在確認卡上按下「確認送出」。</summary>
    Confirm,

    /// <summary>顧客在確認卡上按下「取消」。</summary>
    Reject,

    /// <summary>顧客上傳照片後續跑。</summary>
    PhotosUploaded,
}

/// <summary>展示劇本的處理流程（M6）。資料庫以數值保存，新增項目請加在最後、不要調整順序。</summary>
public enum ScenarioFlow
{
    /// <summary>傳統表單：顧客自己填售後申請表單。</summary>
    Traditional = 1,

    /// <summary>單一 Agent。</summary>
    SingleAgent = 2,

    /// <summary>多 Agent 交接。</summary>
    MultiAgent = 3,
}
