using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 查詢稽核（0.9.131 起）：完整條件（含搜尋原文）進詳細內容檔，資料庫摘要只記使用者輸入的文字「有沒有填」。
/// </summary>
public sealed class ViewAuditTests
{
    [Fact]
    public async Task WriteSearchAsync_ShouldKeepFreeTextOutOfTheDetail_ButPutEverythingInTheContent()
    {
        var audit = new RecordingAuditLogService();
        var user = new CurrentUserService();
        user.CurrentUser.Id = 7;
        user.CurrentUser.Account = "alice";

        await ViewAudit.WriteSearchAsync(audit, user, "Project.Search", "Project",
            AuditSearchCondition.Text("keyword", "王小明"),
            AuditSearchCondition.Of("teams", new List<string> { "研發部", "業務部" }),
            AuditSearchCondition.Of("showDeleted", false),
            AuditSearchCondition.Text("account", ""));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(7, entry.ActorUserId);
        Assert.Equal("hasKeyword=True; teams=研發部, 業務部; showDeleted=false; hasAccount=False", entry.Detail);
        Assert.DoesNotContain("王小明", entry.Detail, StringComparison.Ordinal);
        Assert.Equal(
            string.Join("\n", "查詢條件", "keyword: 王小明", "teams: 研發部, 業務部", "showDeleted: false", "account: （空）"),
            Assert.Single(audit.Contents).Content);
    }
}
