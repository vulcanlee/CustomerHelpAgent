using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace CustomerHelpAgent.Web.Agents.AfterSales;

/// <summary>
/// 模型請求摘要（1.0.5 起）：錄製時記下、回放時比對，判斷「這次送給模型的內容」是否與錄製時相同。
///
/// <list type="number">
/// <item>依訊息順序只取 <see cref="ChatRole.User"/> 的文字與每個 <see cref="FunctionResultContent.Result"/>；
/// <b>不含</b>系統指示（<c>ChatOptions.Instructions</c> 本來就不在訊息裡，system 訊息也略過）、模型自身的輸出（文字與函式呼叫、CallId）、時間戳。</item>
/// <item>工具結果一律轉成同一種 JSON 寫法（中文不跳脫）：當輪的結果是工具剛產生的物件，還原的會話裡則是重新解析的 JSON，兩者要得到同一個字串。</item>
/// <item>正規化會隨重置改變、但不影響劇情的值：日期時間（ISO-8601、<c>yyyy/MM/dd [HH:mm]</c>）、申請編號（<c>AS</c>＋12 碼）、
/// 建立結果的申請 Id（<c>"request_id":123</c>）。目錄資料的 Id 由種子固定（訂單商品等），簽收天數等以重置當下相對計算，都不需正規化；
/// 庫存、資格等真正的資料差異仍會讓摘要不同。</item>
/// <item>結果以 SHA-256 小寫十六進位表示。</item>
/// </list>
/// </summary>
public static partial class ModelRequestDigest
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new(AIJsonUtilities.DefaultOptions)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static string Compute(IEnumerable<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (message.Role == ChatRole.User && content is TextContent text)
                {
                    builder.Append("user:").Append(text.Text).Append('\n');
                }
                else if (content is FunctionResultContent result)
                {
                    builder.Append("result:").Append(Canonical(result.Result)).Append('\n');
                }
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(builder.ToString()))));
    }

    /// <summary>任何結果（物件、字串、JsonElement）都先轉成 JsonElement，再以固定選項寫出。</summary>
    private static string Canonical(object? value)
    {
        var element = value as JsonElement? ?? JsonSerializer.SerializeToElement(value, AIJsonUtilities.DefaultOptions);
        return JsonSerializer.Serialize(element, CanonicalJsonOptions);
    }

    private static string Normalize(string text)
    {
        text = RequestNoPattern().Replace(text, "AS<request-no>");
        text = RequestIdPattern().Replace(text, "\"request_id\":\"<id>\"");
        text = IsoDateTimePattern().Replace(text, "<datetime>");
        return SlashDateTimePattern().Replace(text, "<datetime>");
    }

    [GeneratedRegex(@"AS\d{12}")]
    private static partial Regex RequestNoPattern();

    [GeneratedRegex(@"""request_id""\s*:\s*\d+")]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:\d{2})?)?")]
    private static partial Regex IsoDateTimePattern();

    [GeneratedRegex(@"\d{4}/\d{1,2}/\d{1,2}(?: \d{1,2}:\d{2}(?::\d{2})?)?")]
    private static partial Regex SlashDateTimePattern();
}
