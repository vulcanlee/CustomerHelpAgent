using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 稽核的修改前後差異（0.9.131 起）：寫進詳細內容檔的「欄位：舊值 → 新值」。
/// ⚠️ 最要緊的是排除規則 —— 密碼、版本號這類欄位的值一旦進了內容檔，就會被備份、被管理員讀到。
/// </summary>
public sealed class AuditDiffTests
{
    [Fact]
    public void Compare_ShouldListOnlyChangedScalarFields()
    {
        var before = new Category { Id = 3, Name = "分類A", Description = "舊說明", IsEnabled = true };
        var after = new Category { Id = 3, Name = "分類B", Description = "舊說明", IsEnabled = false };

        var diff = AuditDiff.Compare(before, after);

        Assert.Equal(["Name", "IsEnabled"], diff.ChangedFields);
        Assert.Equal(
            string.Join("\n", "分類 #3 修改", "Name: 分類A → 分類B", "IsEnabled: true → false"),
            diff.Render("分類 #3 修改"));
    }

    [Fact]
    public void Compare_ShouldIgnoreSystemFields_AndNavigationProperties()
    {
        var before = new Project
        {
            Id = 1,
            Title = "專案",
            ConcurrencyStamp = "old",
            CreatedAt = new DateTime(2026, 1, 1),
            UpdatedAt = new DateTime(2026, 1, 1),
            IsDeleted = false,
            Files = [new ProjectFile()],
        };
        var after = new Project
        {
            Id = 2,
            Title = "專案",
            ConcurrencyStamp = "new",
            CreatedAt = new DateTime(2026, 2, 2),
            UpdatedAt = new DateTime(2026, 2, 2),
            IsDeleted = true,
            Files = [],
        };

        Assert.False(AuditDiff.Compare(before, after).HasChanges);
        Assert.Null(AuditDiff.Compare(before, after).Render("x"));
    }

    [Fact]
    public void Compare_WithIgnoredField_AndMasked_ShouldNeverWriteTheValue()
    {
        var before = new MyUser { Account = "alice", Password = "old-hash", Salt = "s1" };
        var after = new MyUser { Account = "alice", Password = "new-hash", Salt = "s2" };

        var text = AuditDiff.Compare(before, after, nameof(MyUser.Password), nameof(MyUser.Salt))
            .Masked(nameof(MyUser.Password), changed: true)
            .Render("使用者 #1 修改");

        Assert.Contains("Password: 已變更（內容不記錄）", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hash", text, StringComparison.Ordinal);
        Assert.DoesNotContain("s1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Value_ShouldTreatNullAndEmptyAsTheSame()
    {
        Assert.False(new AuditDiff().Value("Description", null, string.Empty).HasChanges);
        Assert.Equal("x\nDescription: （空） → 新", new AuditDiff().Value("Description", null, "新").Render("x"));
    }

    [Fact]
    public void Value_WithMultiLineText_ShouldUseABlock()
    {
        var text = new AuditDiff().Value("Description", "第一行\r\n第二行", "第一行\n改過").Render("x");

        Assert.Equal(
            string.Join("\n", "x", "Description:", "----- 修改前 -----", "第一行", "第二行", "----- 修改後 -----", "第一行", "改過", "-----"),
            text);
    }

    [Fact]
    public void Set_ShouldListAddedAndRemovedItems()
    {
        var text = new AuditDiff().Set("Teams", ["研發部", "業務部"], ["研發部", "管理部"]).Render("x");

        Assert.Equal("x\nTeams: 新增 [管理部]；移除 [業務部]", text);
        Assert.False(new AuditDiff().Set("Teams", ["A", "B"], ["B", "A"]).HasChanges);
    }
}
