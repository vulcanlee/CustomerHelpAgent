using System.Text.RegularExpressions;

namespace CustomerHelpAgent.Business.Helpers;

/// <summary>
/// 個資樣式的單一定義（Email、電話、台灣地址）：AI 初審送模型前的遮蔽（<c>PreReviewPrivacy</c>）與顧客長期記憶的內容政策（<see cref="MemoryContentPolicy"/>）共用，
/// 兩邊對「什麼算個資」永遠一致。
/// </summary>
public static partial class PersonalDataPatterns
{
    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")]
    public static partial Regex Email();

    /// <summary>
    /// 手機（09xx-xxx-xxx，可含空白或連字號）、市話（區碼 0x 或 0xx 後接 6～8 碼，可含括號、連字號），
    /// 以及 +886 國際格式（區碼去掉開頭的 0：+886-9xx-xxx-xxx、+886-2-xxxx-xxxx，「+」可省略）。
    /// </summary>
    [GeneratedRegex(@"(?<!\d)(?:09\d{2}[-\s]?\d{3}[-\s]?\d{3}|\(?0\d{1,2}\)?[-\s]?\d{3,4}[-\s]?\d{4}|\+?886[-\s]?9\d{2}[-\s]?\d{3}[-\s]?\d{3}|\+?886[-\s]?\(?[2-8]\d{0,2}\)?[-\s]?\d{3,4}[-\s]?\d{4})(?!\d)")]
    public static partial Regex Phone();

    /// <summary>
    /// 台灣地址的簡單判斷：路／街／巷／弄／段後接數字、數字後接號／樓，或「縣／市」後不遠處接「區／鄉／鎮」。
    /// 寧可多擋（回報成不能記住），也不放過帶地址的偏好。
    /// </summary>
    [GeneratedRegex(@"[路街巷弄段]\s*\d|\d\s*[號樓]|[縣市][^\s]{0,4}[區鄉鎮]")]
    public static partial Regex Address();
}
