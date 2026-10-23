using System.Text.Json;

namespace CustomerHelpAgent.Web.Ai;

/// <summary>
/// Chat Completions 回應的解析。純函式，可用假 JSON 完整測試。
///
/// 刻意用 <see cref="JsonDocument"/> 而非 POCO 反序列化：<c>usage</c> 的子物件
/// （<c>prompt_tokens_details</c> / <c>completion_tokens_details</c>）在不同供應商、
/// 不同 api-version、不同模型上時有時無，<c>TryGetProperty</c> 直接表達「缺就是 null」，
/// 不用為每個可選欄位配一顆 nullable POCO。
/// </summary>
public static class AiChatResponseParser
{
    public static AiChatParseResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var content = string.Empty;
        var finishReason = string.Empty;
        var filterCategory = string.Empty;

        // ⚠️ Azure 在 prompt 被內容過濾時會回 choices: []，所以不能直接取 [0]。
        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];

            // ⚠️ Azure 在回應被內容過濾時，message.content 會是 JSON null 而不是缺欄位，
            // 所以必須檢查 ValueKind 而非只看欄位存在。
            if (choice.TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String)
            {
                content = contentElement.GetString() ?? string.Empty;
            }

            finishReason = ReadString(choice, "finish_reason");
            filterCategory = ReadFilterCategory(choice);
        }

        return new AiChatParseResult
        {
            Content = content,
            FinishReason = finishReason,
            ModelName = ReadString(root, "model"),
            Usage = ReadUsage(root),
            FilterCategory = filterCategory,
        };
    }

    /// <summary>
    /// 讀取錯誤細節。兩家的錯誤形狀相同：
    /// <c>{"error":{"code":"...","message":"...","param":"...","type":"..."}}</c>。
    ///
    /// <para>
    /// <c>message</c> 會截斷至 <see cref="AiUpstreamError.MaxMessageLength"/> 字元 ——
    /// 上游對某些錯誤的說明可能夾帶提示詞片段，而提示詞裡是上百筆日誌。
    /// </para>
    /// </summary>
    public static bool TryGetError(string json, out AiUpstreamError error)
    {
        error = new AiUpstreamError();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.TryGetProperty("error", out var node) == false
                || node.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            error = new AiUpstreamError
            {
                Code = ReadString(node, "code"),
                Param = ReadString(node, "param"),
                Type = ReadString(node, "type"),
                Message = Truncate(ReadString(node, "message")),
            };

            return error.HasAny;
        }
        catch (JsonException)
        {
            // 上游可能回 HTML（例如閘道錯誤頁），當作無法解析。
            return false;
        }
    }

    private static string Truncate(string value)
        => value.Length <= AiUpstreamError.MaxMessageLength
            ? value
            : string.Concat(value.AsSpan(0, AiUpstreamError.MaxMessageLength), "…");

    /// <summary>
    /// 取出回應中 <c>usage</c> 子物件的原始 JSON 文字，供「Token 用量」頁保留完整明細
    /// （供應商日後新增欄位也不會漏接）。
    ///
    /// ⚠️ <b>只取 usage 這一段，絕不回傳整個回應 body</b> —— body 裡有模型產生的內文，
    /// 而本專案的提示詞就是日誌內容。存進任何可視範圍更廣的地方都等於繞過日誌頁的管理員限制。
    /// </summary>
    public static string? ExtractUsageJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object
                ? usage.GetRawText()
                : null;
        }
        catch (JsonException)
        {
            // 上游可能回 HTML（例如閘道錯誤頁），當作沒有 usage。
            return null;
        }
    }

    private static AiTokenUsage? ReadUsage(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var usage) == false || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new AiTokenUsage
        {
            InputCount = ReadInt(usage, "prompt_tokens"),
            OutputCount = ReadInt(usage, "completion_tokens"),
            TotalCount = ReadInt(usage, "total_tokens"),
            CachedInputCount = ReadNestedInt(usage, "prompt_tokens_details", "cached_tokens"),
            ReasoningCount = ReadNestedInt(usage, "completion_tokens_details", "reasoning_tokens"),
        };

        return result.HasAny ? result : null;
    }

    /// <summary>取 content_filter_results 裡第一個 filtered 為 true 的類別名稱。</summary>
    private static string ReadFilterCategory(JsonElement choice)
    {
        if (choice.TryGetProperty("content_filter_results", out var filters) == false
            || filters.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var category in filters.EnumerateObject())
        {
            if (category.Value.ValueKind == JsonValueKind.Object
                && category.Value.TryGetProperty("filtered", out var filtered)
                && filtered.ValueKind == JsonValueKind.True)
            {
                return category.Name;
            }
        }

        return string.Empty;
    }

    private static int? ReadInt(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            ? number
            : null;

    private static int? ReadNestedInt(JsonElement parent, string objectName, string name)
        => parent.TryGetProperty(objectName, out var child) && child.ValueKind == JsonValueKind.Object
            ? ReadInt(child, name)
            : null;

    private static string ReadString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
