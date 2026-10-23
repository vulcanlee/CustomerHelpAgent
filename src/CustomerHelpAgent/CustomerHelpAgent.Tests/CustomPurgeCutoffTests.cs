using CustomerHelpAgent.Web.DataRetention;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 「資料保留與清理」頁的「自訂清除」條件（0.9.134 起）：二選一的驗證，以及依資料表的時間基準換算門檻。
/// 假時鐘：UTC 2026-10-01 01:00 ＝ 本地（UTC+8）2026-10-01 09:00。
/// </summary>
public sealed class CustomPurgeCutoffTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);
    private readonly ManualTimeProvider clock = new(new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData(1)]
    [InlineData(CustomPurgeCutoff.MaxKeepDays)]
    public void KeepDays_InRange_ShouldBeValid(int days)
        => Assert.Null(new CustomPurgeCutoff(days, null).Validate(Today));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(CustomPurgeCutoff.MaxKeepDays + 1)]
    public void KeepDays_OutOfRange_ShouldBeRejected(int days)
        => Assert.NotNull(new CustomPurgeCutoff(days, null).Validate(Today));

    [Fact]
    public void BeforeDate_ShouldAllowTodayButNotTheFuture()
    {
        Assert.Null(new CustomPurgeCutoff(null, Today).Validate(Today));
        Assert.NotNull(new CustomPurgeCutoff(null, Today.AddDays(1)).Validate(Today));
    }

    [Fact]
    public void NeitherOrBoth_ShouldBeRejected()
    {
        Assert.NotNull(new CustomPurgeCutoff(null, null).Validate(Today));
        Assert.NotNull(new CustomPurgeCutoff(30, Today).Validate(Today));
    }

    [Fact]
    public void KeepDays_ShouldFollowEachTimeBasis()
    {
        var cutoff = new CustomPurgeCutoff(30, null);

        Assert.Equal(new DateTime(2026, 9, 1, 9, 0, 0), cutoff.ToThreshold(RetentionTimeBasis.Local, clock));
        Assert.Equal(new DateTime(2026, 9, 1, 1, 0, 0), cutoff.ToThreshold(RetentionTimeBasis.Utc, clock));
        Assert.Equal(new DateTime(2026, 9, 1), cutoff.ToThreshold(RetentionTimeBasis.LocalDate, clock));
    }

    [Fact]
    public void BeforeDate_ShouldBeLocalMidnight_AndConvertedForUtcData()
    {
        var cutoff = new CustomPurgeCutoff(null, new DateOnly(2026, 9, 1));

        Assert.Equal(new DateTime(2026, 9, 1), cutoff.ToThreshold(RetentionTimeBasis.Local, clock));
        Assert.Equal(new DateTime(2026, 9, 1), cutoff.ToThreshold(RetentionTimeBasis.LocalDate, clock));
        Assert.Equal(new DateTime(2026, 8, 31, 16, 0, 0), cutoff.ToThreshold(RetentionTimeBasis.Utc, clock));
    }

    [Fact]
    public void AuditDetail_ShouldOnlyCarryTheConditionAndCount()
    {
        Assert.Equal("source=data-retention; mode=keepDays; value=30; rows=5", new CustomPurgeCutoff(30, null).AuditDetail(5));
        Assert.Equal("source=data-retention; mode=beforeDate; value=2026-09-01; rows=0", new CustomPurgeCutoff(null, new DateOnly(2026, 9, 1)).AuditDetail(0));
    }
}
