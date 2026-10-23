using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Tests;

public sealed class EmailTemplatesTests
{
    /// <summary>放進 HTML 的變數一律要編碼，否則系統名稱或帳號裡的標籤會變成信件內的 HTML。</summary>
    [Fact]
    public void BuildTest_ShouldHtmlEncodeVariables()
    {
        var message = EmailTemplates.BuildTest("alice@example.com", "<b>A&B</b>", DateTime.Now, "Pickup");

        Assert.DoesNotContain("<b>A&B</b>", message.HtmlBody);
        Assert.Contains("&lt;b&gt;A&amp;B&lt;/b&gt;", message.HtmlBody);
        Assert.Contains("<b>A&B</b>", message.TextBody);
    }

    [Fact]
    public void BuildTest_ShouldPrefixSubjectWithSystemName()
    {
        var message = EmailTemplates.BuildTest("alice@example.com", "企業管理平台", DateTime.Now, "Smtp");

        Assert.StartsWith("[企業管理平台]", message.Subject);
        Assert.Equal("alice@example.com", message.To);
        Assert.Equal(EmailKinds.Test, message.Kind);
    }

    [Fact]
    public void BuildPasswordReset_ShouldEncodeAccountAndCarryTheLinkInBothParts()
    {
        const string link = "https://erp.example.com/Auths/ResetPassword?token=abc_DEF-123";

        var message = EmailTemplates.BuildPasswordReset("alice@example.com", "企業管理平台", "<alice>", link, 30);

        Assert.Equal(EmailKinds.PasswordReset, message.Kind);
        Assert.StartsWith("[企業管理平台]", message.Subject);
        Assert.Contains("&lt;alice&gt;", message.HtmlBody);
        Assert.DoesNotContain("<alice>", message.HtmlBody);
        Assert.Contains(link, message.TextBody);
        Assert.Contains($"href=\"{link}\"", message.HtmlBody);
        Assert.Contains("30 分鐘", message.TextBody);
    }

    [Fact]
    public void BuildPasswordChanged_ShouldNameTheAccount()
    {
        var message = EmailTemplates.BuildPasswordChanged("alice@example.com", "企業管理平台", "alice", new DateTime(2026, 9, 24, 14, 0, 0));

        Assert.Equal(EmailKinds.PasswordChanged, message.Kind);
        Assert.Contains("「alice」", message.TextBody);
        Assert.Contains("2026/09/24 14:00:00", message.TextBody);
    }
}
