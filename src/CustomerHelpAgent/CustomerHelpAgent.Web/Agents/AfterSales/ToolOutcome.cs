using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Web.Agents.AfterSales;

/// <summary>
/// 售後 Agent 工具的統一結果信封。工具（<see cref="AfterSalesAgentTools.CreateFunctions"/>）回傳的是以
/// <see cref="JsonOptions"/> 序列化後的 <see cref="JsonElement"/>，模型看到的就是這個信封本身：
/// <c>{"status":"not_found","data":null,"message":"…","retryable":false,"withheld_fields":[…]}</c>。
/// <see cref="WithheldFields"/> 是「刻意不送給模型」的欄位名稱（只有名稱，沒有值），供幕後面板說明。
/// </summary>
public sealed record ToolOutcome(
    ToolOutcomeStatus Status,
    object? Data,
    string Message,
    bool Retryable,
    IReadOnlyList<string> WithheldFields)
{
    /// <summary>給模型的 JSON 格式：屬性與列舉一律小寫蛇形；中文不跳脫（模型讀得懂、也省 token）。</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ToolOutcome Ok(object? data, IReadOnlyList<string>? withheld = null)
        => new(ToolOutcomeStatus.Ok, data, string.Empty, false, withheld ?? []);

    public static ToolOutcome Fail(ToolOutcomeStatus status, string message, bool retryable = false)
        => new(status, null, message, retryable, []);

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, JsonOptions);

    /// <summary>
    /// 把工具函式的回傳值（<see cref="JsonElement"/>、JSON 字串或 <see cref="ToolOutcome"/>）讀回信封；
    /// 讀回的 <see cref="Data"/> 是 <see cref="JsonElement"/>。不是信封回 false。
    /// </summary>
    public static bool TryParse(object? functionResult, [NotNullWhen(true)] out ToolOutcome? outcome)
    {
        outcome = null;
        try
        {
            outcome = functionResult switch
            {
                ToolOutcome x => x,
                JsonElement { ValueKind: JsonValueKind.Object } element => element.Deserialize<ToolOutcome>(JsonOptions),
                string text => JsonSerializer.Deserialize<ToolOutcome>(text, JsonOptions),
                _ => null,
            };
        }
        catch (JsonException)
        {
            outcome = null;
        }

        return outcome is not null;
    }
}
