using Bunit;
using Microsoft.AspNetCore.Components.Web;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Web.Components.Views.Shop.Assistant;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>線上客服的模式切換（M5）：單選群組的語意與鍵盤操作（roving tabindex、方向鍵），回放鎖定時讀得到鎖定原因。</summary>
public sealed class AgentModeSwitchTests : ComponentTestBase
{
    private readonly List<AgentMode> changes = [];

    [Fact]
    public void RovingTabIndex_OnlySelectedInTabOrder()
    {
        var cut = RenderSwitch(AgentMode.Single);

        Assert.Equal("0", cut.Find("[data-mode=single]").GetAttribute("tabindex"));
        Assert.Equal("-1", cut.Find("[data-mode=handoff]").GetAttribute("tabindex"));
        Assert.Equal("radio", cut.Find("[data-mode=single]").GetAttribute("role"));
    }

    [Fact]
    public void ArrowKeys_SelectOtherMode_AndWrap()
    {
        var cut = RenderSwitch(AgentMode.Single);

        cut.Find("[data-mode=single]").KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });
        cut.Find("[data-mode=single]").KeyDown(new KeyboardEventArgs { Key = "ArrowLeft" });
        cut.Find("[data-mode=single]").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        // 右鍵到「多 Agent」；左鍵從第一個循環到最後一個（也是「多 Agent」）；其他鍵不動。
        Assert.Equal([AgentMode.Handoff, AgentMode.Handoff], changes);
    }

    [Fact]
    public void Locked_IgnoresKeys_AndDescribesReason()
    {
        var cut = RenderSwitch(AgentMode.Handoff, locked: true);

        cut.Find("[data-mode=handoff]").KeyDown(new KeyboardEventArgs { Key = "ArrowLeft" });

        Assert.Empty(changes);
        var describedBy = cut.Find("[data-role=mode-switch]").GetAttribute("aria-describedby");
        Assert.False(string.IsNullOrEmpty(describedBy));
        Assert.Contains("回放", cut.Find($"#{describedBy}").TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find("[data-mode=single]").HasAttribute("disabled"));
    }

    [Fact]
    public void NotLocked_NoDescription()
    {
        var cut = RenderSwitch(AgentMode.Single);

        Assert.Null(cut.Find("[data-role=mode-switch]").GetAttribute("aria-describedby"));
        Assert.Empty(cut.FindAll("[data-role=mode-lock-hint]"));
    }

    private IRenderedComponent<AgentModeSwitch> RenderSwitch(AgentMode mode, bool locked = false)
        => Render<AgentModeSwitch>(parameters => parameters
            .Add(x => x.Mode, mode)
            .Add(x => x.Locked, locked)
            .Add(x => x.OnChange, (AgentMode selected) => changes.Add(selected)));
}
