namespace CustomerHelpAgent.Share.Demo;

/// <summary>故障模擬的畫面名稱與說明（1.0.5 起；展示控制台、顧客頁的「故障模擬中」共用，不要各寫一份）。</summary>
public static class DemoFaultLabels
{
    /// <summary>控制台的顯示順序（訂單 → 物流 → 規則 → 權限）。</summary>
    public static IReadOnlyList<DemoFault> Ordered { get; } =
    [
        DemoFault.OrderServiceTimeout,
        DemoFault.OrdersNotFound,
        DemoFault.ShipmentAnomaly,
        DemoFault.PolicyServiceUnavailable,
        DemoFault.PolicyConflict,
        DemoFault.PolicyNotFound,
        DemoFault.PermissionExpired,
    ];

    public static string Name(DemoFault fault) => fault switch
    {
        DemoFault.OrderServiceTimeout => "訂單系統逾時",
        DemoFault.OrdersNotFound => "查無訂單",
        DemoFault.ShipmentAnomaly => "物流異常",
        DemoFault.PolicyServiceUnavailable => "規則服務停擺",
        DemoFault.PolicyConflict => "規則衝突",
        DemoFault.PolicyNotFound => "無適用規則",
        DemoFault.PermissionExpired => "權限過期",
        _ => fault.ToString(),
    };

    public static string Description(DemoFault fault) => fault switch
    {
        DemoFault.OrderServiceTimeout => "讀取訂單要等很久才失敗；Agent 回報「逾時」，不能說顧客沒有這筆訂單。傳統頁面同樣受影響。",
        DemoFault.OrdersNotFound => "我的訂單是空的、單筆訂單查不到。Agent 與傳統頁面同樣受影響。",
        DemoFault.ShipmentAnomaly => "物流狀態改為「配送異常」並多一筆異常事件（只改讀出的結果，不改資料）。",
        DemoFault.PolicyServiceUnavailable => "查詢售後規則直接失敗；Agent 要明說暫時無法核實。",
        DemoFault.PolicyConflict => "同時查到兩個互相衝突的規則版本；Agent 不能送出申請、要轉人工。",
        DemoFault.PolicyNotFound => "查不到適用的售後規則；Agent 要轉人工。",
        DemoFault.PermissionExpired => "Agent 工具的身分檢查一律不通過，程式立即停止這一輪。只影響 Agent，傳統頁面不模擬。",
        _ => string.Empty,
    };
}
