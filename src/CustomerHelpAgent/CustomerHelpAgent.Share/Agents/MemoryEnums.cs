namespace CustomerHelpAgent.Share.Agents;

/// <summary>
/// 顧客長期記憶（M5）的偏好種類。一種偏好只存一筆，新值覆蓋舊值。
/// ⚠️ 記憶只代表「偏好」，不是訂單、規則等業務事實；數值已寫入資料庫，不可調整既有編號。
/// </summary>
public enum MemoryKind
{
    /// <summary>鞋碼（35–46，可有半號）。</summary>
    ShoeSize = 1,

    /// <summary>衣服尺碼（XS–XXL）。</summary>
    ClothingSize = 2,

    /// <summary>偏好顏色（中文顏色詞）。</summary>
    PreferredColor = 3,

    /// <summary>溝通偏好（簡短一句話）。</summary>
    Communication = 4,
}
