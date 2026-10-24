using System.Text.RegularExpressions;
using CustomerHelpAgent.Business.Services.DataAccess;

namespace CustomerHelpAgent.Web.Agents.PreReview;

/// <summary>
/// AI 初審送給模型前的個資遮蔽（所有帶顧客自由文字的模型節點共用：照片的顧客描述、歷史紀錄的對話、摘要讀到的各節點結論）。
/// 申請事實與工具結果本來就不含收件人、地址、手機；這裡處理的是顧客自己打出來的字：
/// <list type="number">
/// <item>以程式查出的原文精確比對（<see cref="AfterSalesRequestService.GetPersonalValuesForRedactionAsync"/>）：
/// 姓名（顧客、收件人）→「此顧客」；帳號、Email、地址、手機 →「［已遮蔽］」。長的先換，避免短值切斷長值。</item>
/// <item>再以樣式遮蔽其他 Email 與電話號碼（手機、市話）→「［已遮蔽］」。</item>
/// </list>
/// ⚠️ 原文只在程式內比對，絕不送給模型、不寫進日誌。結果是確定的（同樣的資料得到同樣的文字），回放的請求摘要才比得起來。
/// </summary>
public static partial class PreReviewPrivacy
{
    public const string Masked = "［已遮蔽］";
    public const string CustomerAlias = "此顧客";

    /// <summary>查出這筆申請的個資原文並遮蔽 <paramref name="text"/>。</summary>
    public static async Task<string> RedactAsync(AfterSalesRequestService requests, int requestId, string text)
    {
        var (names, others) = await requests.GetPersonalValuesForRedactionAsync(requestId);
        return Redact(text, names, others);
    }

    public static string Redact(string text, IReadOnlyCollection<string> names, IReadOnlyCollection<string> others)
    {
        var replacements = names.Select(x => (Value: x, With: CustomerAlias))
            .Concat(others.Select(x => (Value: x, With: Masked)))
            .Where(x => x.Value.Length > 0)
            .OrderByDescending(x => x.Value.Length);
        foreach (var (value, with) in replacements)
        {
            text = text.Replace(value, with, StringComparison.Ordinal);
        }

        text = EmailPattern().Replace(text, Masked);
        return PhonePattern().Replace(text, Masked);
    }

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")]
    private static partial Regex EmailPattern();

    /// <summary>手機（09xx-xxx-xxx，可含空白或連字號）、市話（區碼 0x 或 0xx 後接 6～8 碼，可含括號、連字號）。</summary>
    [GeneratedRegex(@"(?<!\d)(?:09\d{2}[-\s]?\d{3}[-\s]?\d{3}|\(?0\d{1,2}\)?[-\s]?\d{3,4}[-\s]?\d{4})(?!\d)")]
    private static partial Regex PhonePattern();
}
