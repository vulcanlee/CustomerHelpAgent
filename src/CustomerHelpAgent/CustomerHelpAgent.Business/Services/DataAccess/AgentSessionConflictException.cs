namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// 存回會話時版本已被另一個分頁推進（<see cref="AgentSessionStore.SaveStateAsync"/> 的樂觀版本檢查）。
/// 訊息是給顧客看的中文；繼承 <see cref="InvalidOperationException"/>，沿用「Runner 拒絕時訊息直接顯示」的慣例。
/// </summary>
public sealed class AgentSessionConflictException : InvalidOperationException
{
    public const string DefaultMessage = "這段對話已在其他分頁更新，請重新整理頁面。";

    public AgentSessionConflictException()
        : base(DefaultMessage)
    {
    }

    public AgentSessionConflictException(string message)
        : base(message)
    {
    }

    public AgentSessionConflictException(string message, string? createdRequestNo)
        : base(message)
    {
        CreatedRequestNo = createdRequestNo;
    }

    /// <summary>衝突的那一輪已建立的申請編號（確認送出後才衝突）；畫面據此把確認卡標成「已建立」，不可顯示成沒送出。</summary>
    public string? CreatedRequestNo { get; }
}
