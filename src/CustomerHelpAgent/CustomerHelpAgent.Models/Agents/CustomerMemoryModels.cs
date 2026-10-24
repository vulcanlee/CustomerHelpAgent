using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Models.Agents;

/// <summary>顧客的一筆長期記憶（給「我的 AI 記憶」頁與 AI 內容提供者用）。</summary>
public sealed record CustomerMemoryView(int Id, MemoryKind Kind, string Value, DateTime UpdatedAt);

/// <summary>記憶寫入／刪除的結果；<see cref="Message"/> 是給顧客看的中文，<see cref="Memory"/> 在成功寫入時為新值。</summary>
public sealed record MemoryResult(bool Success, string Message, CustomerMemoryView? Memory);
