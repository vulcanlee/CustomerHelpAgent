using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Tests;

/// <summary>交接事件 RawJson 的共用讀取（M5）：Runner 還原、線上客服頁與幕後面板都用它。</summary>
public sealed class HandoffEventDataTests
{
    [Fact]
    public void FromRawJson_ReadsNamesAndReason()
    {
        var data = HandoffEventData.FromRawJson("""{"from":"分流 Agent","to":"品質專員","fromId":"triage","toId":"quality","reason":"顧客反映開膠"}""");

        Assert.Equal(new HandoffEventData("分流 Agent", "品質專員", "顧客反映開膠"), data);
    }

    [Fact]
    public void FromRawJson_ReasonOptional()
        => Assert.Equal(new HandoffEventData("分流 Agent", "品質專員", null), HandoffEventData.FromRawJson("""{"from":"分流 Agent","to":"品質專員","reason":null}"""));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"from":"分流 Agent"}""")]
    [InlineData("""{"from":"分流 Agent","to":1}""")]
    public void FromRawJson_InvalidReturnsNull(string? raw) => Assert.Null(HandoffEventData.FromRawJson(raw));
}
