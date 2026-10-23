using System.ComponentModel.DataAnnotations;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// AI 對話紀錄設定（0.9.72 起）。保留天數寫壞必須在啟動時就失敗（ValidateOnStart），
/// 否則自動過期會悄悄用錯的門檻刪掉資料。
/// </summary>
public sealed class AiCallLogSettingsTests
{
    [Fact]
    public void Defaults_ShouldRecordAndKeepNinetyDays()
    {
        var settings = new AiCallLogSettings();

        Assert.True(settings.Enabled);
        Assert.Equal(90, settings.RetentionDays);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3650, true)]
    [InlineData(3651, false)]
    public void RetentionDays_ShouldBeWithinRange(int retentionDays, bool expectedValid)
    {
        var settings = new AiCallLogSettings { RetentionDays = retentionDays };

        var valid = Validator.TryValidateObject(settings, new ValidationContext(settings), [], validateAllProperties: true);

        Assert.Equal(expectedValid, valid);
    }
}
