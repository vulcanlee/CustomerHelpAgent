using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Diagnostics;

/// <summary>
/// 慢資料庫指令（0.9.79 起，LOG-21）：單一指令超過 <see cref="SlowOperationSettings.DbCommandMs"/> 就記 Warning。
///
/// ⚠️ 只記指令類型（SELECT／INSERT／UPDATE／DELETE…）與耗時，<b>絕不記 SQL 本文與參數</b> ——
/// 參數裡就是使用者資料（名稱、帳號、Email）。要看是哪一句，請用追蹤碼到 /logs 找同一次操作的前後日誌。
///
/// Singleton：<c>AddDbContextFactory</c> 的選項是單例，攔截器跟著是單例。
/// 耗時取 EF Core 提供的 <c>Duration</c>；查詢的耗時不含之後逐列讀取資料的時間。
/// </summary>
public sealed class SlowDbCommandInterceptor : DbCommandInterceptor
{
    private readonly IOptionsMonitor<SlowOperationSettings> options;
    private readonly ILogger<SlowDbCommandInterceptor> logger;

    public SlowDbCommandInterceptor(IOptionsMonitor<SlowOperationSettings> options, ILogger<SlowDbCommandInterceptor> logger)
    {
        this.options = options;
        this.logger = logger;
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Check(command, eventData.Duration);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Check(command, eventData.Duration);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Check(command, eventData.Duration);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Check(command, eventData.Duration);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Check(command, eventData.Duration);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Check(command, eventData.Duration);
        return ValueTask.FromResult(result);
    }

    private void Check(DbCommand command, TimeSpan duration)
    {
        var threshold = options.CurrentValue.DbCommandMs;
        if (SlowOperationSettings.IsSlow(duration, threshold))
        {
            logger.LogWarning(
                "Slow database command. CommandKind={CommandKind}, ElapsedMilliseconds={ElapsedMilliseconds}, ThresholdMilliseconds={ThresholdMilliseconds}",
                CommandKind(command.CommandText),
                (long)duration.TotalMilliseconds,
                threshold);
        }
    }

    /// <summary>SQL 的第一個關鍵字（例如 SELECT）。只取這個，不取任何其他內容。</summary>
    internal static string CommandKind(string? commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return "UNKNOWN";
        }

        var span = commandText.AsSpan().TrimStart();
        var end = 0;
        while (end < span.Length && char.IsLetter(span[end]))
        {
            end++;
        }

        return end == 0 ? "UNKNOWN" : span[..end].ToString().ToUpperInvariant();
    }
}
