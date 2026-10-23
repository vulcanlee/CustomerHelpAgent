using Microsoft.AspNetCore.Http;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Diagnostics;
using CustomerHelpAgent.Web.Extensions;
using CustomerHelpAgent.Web.Health;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 慢操作判斷（LOG-21）與日誌管線自我監控（LOG-22）。
/// </summary>
public sealed class LoggingPipelineHealthTests
{
    [Theory]
    [InlineData(1001, 1000, true)]
    [InlineData(1000, 1000, false)]
    [InlineData(999999, 0, false)]     // 0＝停用
    public void IsSlow_ShouldRespectThresholdAndDisable(int elapsedMs, int thresholdMs, bool expected)
    {
        Assert.Equal(expected, SlowOperationSettings.IsSlow(TimeSpan.FromMilliseconds(elapsedMs), thresholdMs));
    }

    [Theory]
    [InlineData("SELECT \"c\".\"Id\" FROM \"Category\" WHERE \"c\".\"Name\" = @p0", "SELECT")]
    [InlineData("  insert into x values (@p0)", "INSERT")]
    [InlineData("", "UNKNOWN")]
    [InlineData(null, "UNKNOWN")]
    [InlineData("-- comment", "UNKNOWN")]
    public void CommandKind_ShouldOnlyExposeTheFirstKeyword(string? sql, string expected)
    {
        // 只取第一個關鍵字：SQL 本文與參數（使用者資料）絕不進日誌。
        Assert.Equal(expected, SlowDbCommandInterceptor.CommandKind(sql));
    }

    [Theory]
    [InlineData("/_blazor", true)]
    [InlineData("/_blazor/negotiate", true)]
    [InlineData("/api/Category", false)]
    [InlineData("/projects", false)]
    public void BlazorConnection_ShouldBeExcludedFromSlowRequests(string path, bool expected)
    {
        // /_blazor 的 WebSocket 連線持續整個 circuit，不排除的話每個使用者離開時都是一筆慢請求。
        Assert.Equal(expected, ApplicationBuilderExtensions.IsLongLivedConnection(new PathString(path)));
    }

    [Fact]
    public void Monitor_ShouldAccumulateCounters()
    {
        var monitor = new LoggingPipelineMonitor();
        var now = new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero);

        monitor.RecordWriteFailure(now);
        monitor.RecordWriteFailure(now.AddMinutes(1));
        monitor.RecordClientErrorDropped();
        monitor.RecordAlertQueueFailure();
        monitor.RecordAlertSendFailure();
        monitor.RecordNLogInternalError(now);

        var snapshot = monitor.Snapshot();
        Assert.Equal(2, snapshot.WriteFailures);
        Assert.Equal(now.AddMinutes(1), snapshot.LastWriteFailureAt);
        Assert.Equal(1, snapshot.ClientErrorsDropped);
        Assert.Equal(1, snapshot.AlertQueueFailures);
        Assert.Equal(1, snapshot.AlertSendFailures);
        Assert.Equal(1, snapshot.NLogInternalErrors);
    }

    [Fact]
    public void Evaluate_AllClear_ShouldBeHealthy()
    {
        var item = SystemHealthService.EvaluateLoggingPipeline(Empty, 0, 1000, 0, 50);

        Assert.Equal(SystemHealthStatus.Healthy, item.Status);
        Assert.Equal("日誌管線", item.Name);
    }

    [Fact]
    public void Evaluate_ClientErrorDropsOnly_ShouldStayHealthy()
    {
        // 前端回報被限流擋下是照設計運作，不算管線出問題。
        var item = SystemHealthService.EvaluateLoggingPipeline(Empty with { ClientErrorsDropped = 30 }, 0, 1000, 0, 50);

        Assert.Equal(SystemHealthStatus.Healthy, item.Status);
    }

    [Theory]
    [MemberData(nameof(DegradedCases))]
    public void Evaluate_AnyPipelineProblem_ShouldDegrade(LoggingPipelineSnapshot snapshot, int queueLength, long dropped, double? diskGb)
    {
        var item = SystemHealthService.EvaluateLoggingPipeline(snapshot, queueLength, 1000, dropped, diskGb);

        Assert.Equal(SystemHealthStatus.Degraded, item.Status);
    }

    public static TheoryData<LoggingPipelineSnapshot, int, long, double?> DegradedCases() => new()
    {
        { Empty with { WriteFailures = 1 }, 0, 0, 50 },
        { Empty with { AlertSendFailures = 1 }, 0, 0, 50 },
        { Empty with { AlertQueueFailures = 1 }, 0, 0, 50 },
        { Empty with { NLogInternalErrors = 1 }, 0, 0, 50 },
        { Empty, 800, 0, 50 },
        { Empty, 0, 3, 50 },
        { Empty, 0, 0, 0.5 },
    };

    [Theory]
    [InlineData(1000, 50.0)]
    [InlineData(0, 0.1)]
    public void Evaluate_QueueFullOrDiskAlmostFull_ShouldBeUnhealthy(int queueLength, double diskGb)
    {
        var item = SystemHealthService.EvaluateLoggingPipeline(Empty, queueLength, 1000, 0, diskGb);

        Assert.Equal(SystemHealthStatus.Unhealthy, item.Status);
    }

    private static LoggingPipelineSnapshot Empty => new(0, null, 0, 0, 0, 0, null);
}
