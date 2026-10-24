using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Web.Agents.AfterSales;

namespace CustomerHelpAgent.Web.Components.Views.Shop.Assistant;

/// <summary>步驟時間軸（顧客端幕後面板與後台執行紀錄窗共用）；展開狀態以列號記在元件內。</summary>
public partial class AgentStepTimeline
{
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HashSet<int> expandedRows = [];

    [Parameter, EditorRequired]
    public IReadOnlyList<AgentTraceEvent> Events { get; set; } = [];

    [Parameter]
    public string EmptyText { get; set; } = string.Empty;

    protected override void OnParametersSet()
    {
        // 清單被換掉（開新對話、換一筆紀錄）時，展開的列號已經對不上了。
        expandedRows.RemoveWhere(index => index >= Events.Count);
    }

    /// <summary>停止列底色、程式提醒（<see cref="AgentStepKind.Warning"/>）標紅、多 Agent 交接（<see cref="AgentStepKind.Handoff"/>）專屬樣式；其他列沒有額外樣式。</summary>
    private static string? RowClass(AgentStepKind kind) => kind switch
    {
        AgentStepKind.Stop => "is-stop",
        AgentStepKind.Warning => "is-warning",
        AgentStepKind.Handoff => "is-handoff",
        _ => null,
    };

    /// <summary>JSON 排版後顯示（中文不跳脫）；不是 JSON 就原樣顯示。</summary>
    private static string FormatRaw(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(document.RootElement, IndentedJson);
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private void ToggleRaw(int index)
    {
        if (!expandedRows.Remove(index))
        {
            expandedRows.Add(index);
        }
    }
}
