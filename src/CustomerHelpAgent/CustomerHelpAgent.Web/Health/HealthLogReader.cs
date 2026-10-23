using CustomerHelpAgent.Web.Diagnostics;

namespace CustomerHelpAgent.Web.Health;

public interface IHealthLogReader
{
    HealthLogTail ReadLatestLines(int lineCount);
}

public sealed class HealthLogReader : IHealthLogReader
{
    private readonly INLogFilePathResolver pathResolver;
    private readonly ILogger<HealthLogReader> logger;

    public HealthLogReader(INLogFilePathResolver pathResolver, ILogger<HealthLogReader> logger)
    {
        this.pathResolver = pathResolver;
        this.logger = logger;
    }

    public HealthLogTail ReadLatestLines(int lineCount)
    {
        var logFilePath = GetTodayLogFilePath();
        if (string.IsNullOrWhiteSpace(logFilePath))
        {
            return new HealthLogTail
            {
                FilePath = string.Empty,
                Lines = [],
                Status = SystemHealthStatus.Unhealthy,
                Message = "NLog:BasePath 未設定，無法定位日誌檔案。"
            };
        }

        if (!File.Exists(logFilePath))
        {
            return new HealthLogTail
            {
                FilePath = logFilePath,
                Lines = [],
                Status = SystemHealthStatus.Degraded,
                Message = "今日日誌檔案尚未建立。"
            };
        }

        try
        {
            var lines = ReadTail(logFilePath, lineCount);
            return new HealthLogTail
            {
                FilePath = logFilePath,
                Lines = lines,
                Status = SystemHealthStatus.Healthy,
                Message = $"已讀取最後 {lines.Count} 筆日誌。"
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read the latest log lines for the health report.");
            return new HealthLogTail
            {
                FilePath = logFilePath,
                Lines = [],
                Status = SystemHealthStatus.Unhealthy,
                Message = $"讀取日誌失敗：{ex.GetType().Name}。"
            };
        }
    }

    public string GetTodayLogFilePath()
        => pathResolver.GetLogFilePath(DateOnly.FromDateTime(DateTime.Today));

    private static IReadOnlyList<string> ReadTail(string filePath, int lineCount)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var queue = new Queue<string>();

        while (reader.ReadLine() is { } line)
        {
            queue.Enqueue(line);
            while (queue.Count > lineCount)
            {
                queue.Dequeue();
            }
        }

        return queue.ToList();
    }
}
