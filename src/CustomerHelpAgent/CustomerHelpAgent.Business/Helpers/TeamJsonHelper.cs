using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CustomerHelpAgent.Business.Helpers;

/// <summary>
/// 角色「預設團隊」以 JSON 字串陣列儲存於 RoleView.DefaultTeamsJson 的序列化輔助。
/// </summary>
public static class TeamJsonHelper
{
    /// <summary>序列化為 JSON（去頭尾空白、捨棄空白、忽略大小寫去重，保留原順序）。</summary>
    public static string Serialize(IEnumerable<string>? teams)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<string>();
        foreach (var team in teams ?? [])
        {
            var trimmed = (team ?? string.Empty).Trim();
            if (trimmed.Length > 0 && seen.Add(trimmed))
            {
                cleaned.Add(trimmed);
            }
        }

        return JsonSerializer.Serialize(cleaned);
    }

    /// <summary>從 JSON 還原團隊清單；無效或空白回傳空清單。</summary>
    /// <remarks>
    /// 拿得到 logger 的呼叫端請傳入：JSON 壞掉時回傳空清單等於「沒有任何團隊」，
    /// 使用者會默默失去權限，必須留下紀錄（LOG-08）。AutoMapper 運算式內無法傳入，維持不傳。
    /// </remarks>
    public static List<string> Deserialize(string? json, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to parse team JSON; treating it as no teams. JsonLength={JsonLength}", json.Length);
            return [];
        }
    }
}
