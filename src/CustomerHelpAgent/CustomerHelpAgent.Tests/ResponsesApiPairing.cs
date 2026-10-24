using Microsoft.Extensions.AI;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 比照 Azure／OpenAI Responses API 對送出訊息的檢查（實機會回 HTTP 400）：每個工具結果（<c>function_call_output</c>）前面都要有同一個
/// CallId 的工具呼叫（<c>function_call</c>），每個工具呼叫後面也都要有它的結果。腳本化假模型預設開啟這個檢查，
/// 讓「送出沒有對應呼叫的工具結果」這類只在實機才會出錯的缺陷在自動測試就被抓到（M5 實機驗證劇本 H 的缺陷）。
/// 同一個 CallId 的呼叫或結果出現兩次也一樣擋下（實機同樣會回 HTTP 400）。
/// </summary>
internal static class ResponsesApiPairing
{
    /// <summary>不成對時拋出的例外訊息開頭（測試以它辨認是這個檢查擋下的）。</summary>
    public const string ErrorPrefix = "Simulated Responses API HTTP 400: ";

    public static void EnsurePaired(IEnumerable<ChatMessage> messages)
    {
        var called = new HashSet<string>(StringComparer.Ordinal);
        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var content in messages.SelectMany(m => m.Contents))
        {
            switch (content)
            {
                case FunctionCallContent call when !called.Add(call.CallId):
                    throw new InvalidOperationException($"{ErrorPrefix}Duplicate tool call with call_id {call.CallId}.");
                case FunctionResultContent result when !called.Contains(result.CallId):
                    throw new InvalidOperationException($"{ErrorPrefix}No tool call found for function call output with call_id {result.CallId}.");
                case FunctionResultContent result when !answered.Add(result.CallId):
                    throw new InvalidOperationException($"{ErrorPrefix}Duplicate tool output for call_id {result.CallId}.");
            }
        }

        if (called.FirstOrDefault(x => !answered.Contains(x)) is { } orphan)
        {
            throw new InvalidOperationException($"{ErrorPrefix}No tool output found for function call {orphan}.");
        }
    }
}
