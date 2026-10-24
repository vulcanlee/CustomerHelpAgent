namespace CustomerHelpAgent.Share.Agents;

/// <summary>顧客長期記憶（M5）偏好種類的中文標籤（記憶注入、確認卡、幕後面板與「我的 AI 記憶」頁共用，避免各寫一份而漂移）。</summary>
public static class MemoryLabels
{
    /// <summary>種類名稱，例如「鞋碼」。</summary>
    public static string KindText(MemoryKind kind) => kind switch
    {
        MemoryKind.ShoeSize => "鞋碼",
        MemoryKind.ClothingSize => "衣服尺碼",
        MemoryKind.PreferredColor => "顏色",
        MemoryKind.Communication => "溝通",
        _ => kind.ToString(),
    };

    /// <summary>偏好的完整名稱，例如「鞋碼偏好」。</summary>
    public static string PreferenceText(MemoryKind kind) => KindText(kind) + "偏好";
}
