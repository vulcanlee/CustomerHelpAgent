using Microsoft.AspNetCore.Components;
using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Web.Components.Views.Shop.Assistant;

public partial class AgentSourceTag
{
    [Parameter, EditorRequired]
    public AgentDataSource Source { get; set; }

    private string Label => Source switch
    {
        AgentDataSource.Conversation => "對話",
        AgentDataSource.Database => "資料庫",
        AgentDataSource.Policy => "規則",
        AgentDataSource.Memory => "記憶",
        AgentDataSource.Program => "程式",
        _ => Source.ToString(),
    };
}
