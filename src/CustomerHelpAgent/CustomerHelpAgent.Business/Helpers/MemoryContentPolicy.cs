using System.Globalization;
using System.Text.RegularExpressions;
using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Business.Helpers;

/// <summary>
/// 顧客長期記憶（M5）的內容政策：記憶只代表「偏好」，不是業務事實，也不收個資。
/// 由程式重核（不信任模型已檢查過）：
/// <list type="bullet">
/// <item>鞋碼 35–46（可有半號 .5）；衣服尺碼 XS／S／M／L／XL／XXL；顏色為已知色名（黑、深藍…）或「1–3 字＋色」（酒紅色），6 字以內；溝通偏好 40 字以內、不含換行或控制字元。</item>
/// <item>一律拒絕電話、Email、地址樣式（<see cref="PersonalDataPatterns"/>），以及呼叫端提供的顧客姓名、帳號（不分大小寫的完整包含；長度不足 2 的不比對，避免誤殺）。</item>
/// </list>
/// 回傳 <c>Normalized</c> 是要存的值（去頭尾空白，尺碼轉大寫）；拒絕時 <c>Reason</c> 是給顧客看的中文。
/// </summary>
public static partial class MemoryContentPolicy
{
    public const int MaxColorLength = 6;
    public const int MaxCommunicationLength = 40;
    private const int MinPersonalValueLength = 2;

    private static readonly HashSet<string> ClothingSizes = new(StringComparer.Ordinal) { "XS", "S", "M", "L", "XL", "XXL" };

    public static (bool Ok, string? Normalized, string? Reason) Validate(
        MemoryKind kind, string? value, IReadOnlyCollection<string>? personalValues = null)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return Reject("請提供要記住的內容。");
        }

        if (text.Any(IsForbiddenChar))
        {
            return Reject("內容不能包含換行、不可見或特殊控制字元。");
        }

        if (PersonalDataPatterns.Phone().IsMatch(text)
            || PersonalDataPatterns.Email().IsMatch(text)
            || PersonalDataPatterns.Address().IsMatch(text))
        {
            return Reject("記憶只能記錄偏好，不能包含電話、Email 或地址。");
        }

        if (personalValues is not null
            && personalValues.Any(x => x is not null && x.Trim().Length >= MinPersonalValueLength
                && text.Contains(x.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return Reject("記憶只能記錄偏好，不能包含姓名或帳號。");
        }

        return kind switch
        {
            MemoryKind.ShoeSize => ShoeSizePattern().IsMatch(text) ? Accept(text) : Reject("鞋碼請提供 35 到 46 之間的數字，可有半號（例如 42.5）。"),
            MemoryKind.ClothingSize => ClothingSizes.Contains(text.ToUpperInvariant()) ? Accept(text.ToUpperInvariant()) : Reject("衣服尺碼請提供 XS、S、M、L、XL 或 XXL。"),
            MemoryKind.PreferredColor => IsColorWord(text)
                ? Accept(text)
                : Reject($"顏色請用常見色名（例如「黑」「深藍」）或「某色」（例如「酒紅色」），{MaxColorLength} 字以內。"),
            MemoryKind.Communication => text.Length <= MaxCommunicationLength
                ? Accept(text)
                : Reject($"溝通偏好請在 {MaxCommunicationLength} 字以內。"),
            _ => Reject("不支援的記憶種類。"),
        };
    }

    /// <summary>控制字元、行／段落分隔符（U+2028／2029）與格式字元（零寬字元、雙向覆寫等）。</summary>
    private static bool IsForbiddenChar(char c)
        => char.IsControl(c)
            || char.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Format;

    private static readonly HashSet<string> KnownColors = new(StringComparer.Ordinal)
    {
        "黑", "白", "紅", "藍", "綠", "黃", "灰", "粉", "紫", "橘", "橙", "棕", "咖啡", "米", "米白", "卡其", "金", "銀", "駝", "褐", "藏青",
    };

    /// <summary>已知色名（可加「深」「淺」），或「1–3 字＋色」；6 字以內。其他一律不收，避免把任意句子存成「顏色」。</summary>
    private static bool IsColorWord(string text)
    {
        if (text.Length > MaxColorLength || !ChineseOnlyPattern().IsMatch(text))
        {
            return false;
        }

        if (KnownColors.Contains(text) || (text.Length > 1 && text[0] is '深' or '淺' && KnownColors.Contains(text[1..])))
        {
            return true;
        }

        return text.Length is >= 2 and <= 4 && text[^1] == '色';
    }

    private static (bool Ok, string? Normalized, string? Reason) Accept(string normalized) => (true, normalized, null);

    private static (bool Ok, string? Normalized, string? Reason) Reject(string reason) => (false, null, reason);

    /// <summary>35–46 的整數，或 35–45 後接 .5（46.5 超出範圍）。</summary>
    [GeneratedRegex(@"^(?:(?:3[5-9]|4[0-5])(?:\.5)?|46)$")]
    private static partial Regex ShoeSizePattern();

    [GeneratedRegex(@"^[一-鿿]+$")]
    private static partial Regex ChineseOnlyPattern();
}
