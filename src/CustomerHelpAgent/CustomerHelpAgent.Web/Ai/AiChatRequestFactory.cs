using System.Text;
using System.Text.Json;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Ai;

/// <summary>
/// 組出 Chat Completions 的請求 body。
///
/// 用 <see cref="Utf8JsonWriter"/> 明寫而非 POCO 序列化：欄位很少，而且需要
/// 「Temperature 為 null 就整個欄位都不出現」這種條件輸出，用 POCO 反而要多帶一組
/// JsonSerializerOptions 與屬性標註。純函式，測試可直接比對輸出字串。
/// </summary>
public static class AiChatRequestFactory
{
    public static string CreateRequestJson(AiSettings settings, string systemPrompt, string userMessage)
        => CreateRequestJson(
            settings,
            [new AiChatMessage(AiChatRoles.System, systemPrompt), new AiChatMessage(AiChatRoles.User, userMessage)]);

    /// <summary>整段對話的多載（0.9.68 起，AI 例外分析的多輪追問用）。訊息依原順序輸出。</summary>
    public static string CreateRequestJson(AiSettings settings, IReadOnlyList<AiChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(messages);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            // 兩家都放在 body：Azure 填部署名稱、OpenAI 填模型 id，共用同一個設定。
            writer.WriteString("model", settings.Model);

            writer.WriteStartArray("messages");
            foreach (var message in messages)
            {
                WriteMessage(writer, message.Role, message.Content);
            }

            writer.WriteEndArray();

            // 用 max_completion_tokens 而非 max_tokens：後者自 Azure api-version 2024-10-21
            // 起標示為 deprecated，而且 o 系列推論模型只接受前者。
            //
            // ⚠️ 預設不送，由模型自己決定。這個額度同時涵蓋推論模型的思考 token，
            // 設太小會在產出任何可見文字之前就耗盡 —— 拿到空回應，費用照付。
            // 非正數視同沒設定（比夾到某個魔術數字誠實：那個數字對推論模型一樣不夠）。
            if (settings.MaxOutputTokens is int maxOutputTokens && maxOutputTokens > 0)
            {
                writer.WriteNumber("max_completion_tokens", maxOutputTokens);
            }

            // 推論模型不接受 temperature，所以設定為 null 時整個欄位都不送。
            if (settings.Temperature is double temperature)
            {
                writer.WriteNumber("temperature", temperature);
            }

            // 唯讀彈出視窗、不做串流。明寫 false 讓意圖清楚。
            writer.WriteBoolean("stream", false);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());

        static void WriteMessage(Utf8JsonWriter writer, string role, string content)
        {
            writer.WriteStartObject();
            writer.WriteString("role", role);
            writer.WriteString("content", content);
            writer.WriteEndObject();
        }
    }
}
