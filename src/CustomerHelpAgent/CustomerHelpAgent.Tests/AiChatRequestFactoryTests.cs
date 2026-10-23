using System.Text.Json;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 請求 body 組裝測試。0.9.68 起多了「整段對話」的多載（AI 例外分析的多輪追問），
/// 舊的「system + user」多載改為委派過去，輸出必須一字不差。
/// </summary>
public sealed class AiChatRequestFactoryTests
{
    [Fact]
    public void CreateRequestJson_WithMessages_ShouldKeepRoleAndOrder()
    {
        var settings = new AiSettings { Model = "gpt-x" };
        AiChatMessage[] messages =
        [
            new(AiChatRoles.System, "sys"),
            new(AiChatRoles.User, "q1"),
            new(AiChatRoles.Assistant, "a1"),
            new(AiChatRoles.User, "q2"),
        ];

        var json = AiChatRequestFactory.CreateRequestJson(settings, messages);

        using var document = JsonDocument.Parse(json);
        var items = document.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(
            ["system:sys", "user:q1", "assistant:a1", "user:q2"],
            items.Select(item => $"{item.GetProperty("role").GetString()}:{item.GetProperty("content").GetString()}"));
        Assert.Equal("gpt-x", document.RootElement.GetProperty("model").GetString());
        Assert.False(document.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public void CreateRequestJson_LegacyOverload_ShouldMatchMessagesOverload()
    {
        var settings = new AiSettings { Model = "gpt-x", Temperature = 0.2, MaxOutputTokens = 100 };

        var legacy = AiChatRequestFactory.CreateRequestJson(settings, "sys", "user");
        var messages = AiChatRequestFactory.CreateRequestJson(
            settings, [new AiChatMessage(AiChatRoles.System, "sys"), new AiChatMessage(AiChatRoles.User, "user")]);

        Assert.Equal(legacy, messages);
    }
}
