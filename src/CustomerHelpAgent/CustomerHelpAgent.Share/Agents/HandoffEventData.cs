using System.Text.Json;

namespace CustomerHelpAgent.Share.Agents;

/// <summary>
/// 多 Agent 交接事件（<see cref="AgentStepKind.Handoff"/>，M5）RawJson <c>{ from, to, fromId, toId, reason }</c> 的讀取：
/// <see cref="From"/>／<see cref="To"/> 是顯示名稱（例如「分流 Agent」「品質專員」），<see cref="Reason"/> 是交接理由（可為 null）。
/// Runner 還原對話、線上客服頁的串流預覽與幕後面板的交接列共用，避免各寫一份而漂移。
/// </summary>
public sealed record HandoffEventData(string From, string To, string? Reason)
{
    /// <summary>讀取交接事件的 RawJson；不是 JSON 物件或缺少 <c>from</c>／<c>to</c> 字串時回 null。</summary>
    public static HandoffEventData? FromRawJson(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && Text(root, "from") is { } from && Text(root, "to") is { } to
                ? new HandoffEventData(from, to, Text(root, "reason"))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
