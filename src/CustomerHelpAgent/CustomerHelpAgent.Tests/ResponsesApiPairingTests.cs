using Microsoft.Extensions.AI;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 假模型共用的 Responses API 成對檢查（<see cref="ResponsesApiPairing"/>）本身：成對才放行；孤兒結果、沒有結果的呼叫，
/// 以及同一個 CallId 的呼叫或結果出現兩次（實機一樣會回 HTTP 400）都要擋下。
/// </summary>
public sealed class ResponsesApiPairingTests
{
    private static ChatMessage Call(string id) => new(ChatRole.Assistant, [new FunctionCallContent(id, "query_my_orders")]);

    private static ChatMessage Result(string id) => new(ChatRole.Tool, [new FunctionResultContent(id, "ok")]);

    [Fact]
    public void Paired_Passes()
        => ResponsesApiPairing.EnsurePaired([new ChatMessage(ChatRole.User, "hi"), Call("a"), Result("a"), Call("b"), Result("b")]);

    [Fact]
    public void OrphanResult_Throws()
        => AssertRejected([Result("a")], "a");

    [Fact]
    public void CallWithoutResult_Throws()
        => AssertRejected([Call("a")], "a");

    [Fact]
    public void DuplicateCall_Throws()
        => AssertRejected([Call("a"), Result("a"), Call("a"), Result("a")], "Duplicate tool call");

    [Fact]
    public void DuplicateResult_Throws()
        => AssertRejected([Call("a"), Result("a"), Result("a")], "Duplicate tool output");

    private static void AssertRejected(ChatMessage[] messages, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ResponsesApiPairing.EnsurePaired(messages));
        Assert.StartsWith(ResponsesApiPairing.ErrorPrefix, error.Message, StringComparison.Ordinal);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }
}
