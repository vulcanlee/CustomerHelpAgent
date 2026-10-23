using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.Web.Health;
using CustomerHelpAgent.Web.Diagnostics;

namespace CustomerHelpAgent.Tests;

public sealed class SystemHealthTests
{
    [Fact]
    public void CalculateScore_AllHealthy_ShouldReturnGreen100()
    {
        var items = new[]
        {
            CreateItem("A", 50, SystemHealthStatus.Healthy),
            CreateItem("B", 50, SystemHealthStatus.Healthy)
        };

        var score = SystemHealthScoreCalculator.CalculateScore(items);

        Assert.Equal(100, score);
        Assert.Equal(SystemHealthLight.Green, SystemHealthScoreCalculator.GetLight(score));
        Assert.Equal(SystemHealthStatus.Healthy, SystemHealthScoreCalculator.GetStatus(score));
    }

    [Fact]
    public void CalculateScore_DegradedRange_ShouldReturnYellow()
    {
        var items = new[]
        {
            CreateItem("A", 80, SystemHealthStatus.Healthy),
            CreateItem("B", 20, SystemHealthStatus.Unhealthy)
        };

        var score = SystemHealthScoreCalculator.CalculateScore(items);

        Assert.Equal(80, score);
        Assert.Equal(SystemHealthLight.Yellow, SystemHealthScoreCalculator.GetLight(score));
        Assert.Equal(SystemHealthStatus.Degraded, SystemHealthScoreCalculator.GetStatus(score));
    }

    [Fact]
    public void CalculateScore_UnhealthyRange_ShouldReturnRed()
    {
        var items = new[]
        {
            CreateItem("A", 60, SystemHealthStatus.Healthy),
            CreateItem("B", 40, SystemHealthStatus.Unhealthy)
        };

        var score = SystemHealthScoreCalculator.CalculateScore(items);

        Assert.Equal(60, score);
        Assert.Equal(SystemHealthLight.Red, SystemHealthScoreCalculator.GetLight(score));
        Assert.Equal(SystemHealthStatus.Unhealthy, SystemHealthScoreCalculator.GetStatus(score));
    }

    [Theory]
    [InlineData(SystemHealthStatus.Healthy, SystemHealthLight.Green)]
    [InlineData(SystemHealthStatus.Degraded, SystemHealthLight.Yellow)]
    [InlineData(SystemHealthStatus.Unhealthy, SystemHealthLight.Red)]
    public void GetLight_ItemStatus_ShouldMapTrafficLight(SystemHealthStatus status, SystemHealthLight expected)
    {
        Assert.Equal(expected, SystemHealthScoreCalculator.GetLight(status));
    }

    [Fact]
    public void HealthLogReader_ReadLatestLines_ShouldReturnLast100Lines()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "CustomerHelpAgentHealthTests", Guid.NewGuid().ToString("N"));
        var logDirectory = Path.Combine(rootPath, typeof(CustomerHelpAgent.Web.Program).Namespace!);
        Directory.CreateDirectory(logDirectory);
        var logFile = Path.Combine(logDirectory, $"{typeof(CustomerHelpAgent.Web.Program).Namespace}-logfile-{DateTime.Today:yyyy-MM-dd}.log");
        File.WriteAllLines(logFile, Enumerable.Range(1, 150).Select(index => $"line-{index}"));

        try
        {
            var reader = CreateReader(rootPath);

            var tail = reader.ReadLatestLines(100);

            Assert.Equal(SystemHealthStatus.Healthy, tail.Status);
            Assert.Equal(100, tail.Lines.Count);
            Assert.Equal("line-51", tail.Lines.First());
            Assert.Equal("line-150", tail.Lines.Last());
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public void HealthLogReader_MissingFile_ShouldReturnDegraded()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "CustomerHelpAgentHealthTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        try
        {
            var reader = CreateReader(rootPath);

            var tail = reader.ReadLatestLines(100);

            Assert.Equal(SystemHealthStatus.Degraded, tail.Status);
            Assert.Empty(tail.Lines);
            Assert.Contains("尚未建立", tail.Message);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    /// <summary>
    /// 守住健康檢查的權重總和。
    ///
    /// SystemHealthService 本身沒有任何測試，權重是寫死在各 Check 方法裡的字面值 ——
    /// 改錯或漏改不會有任何紅燈，但整份報告的百分比意義會悄悄變掉。
    /// 這個測試用反射把字面值撈出來核對，讓「改了權重卻忘了更新文件」至少會被擋一次。
    ///
    /// ⚠️ 資料庫那項的權重 25 在原始碼裡出現三次（三個 return 分支），改動時特別容易漏。
    /// </summary>
    [Fact]
    public void CheckWeights_ShouldSumTo150()
    {
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["網站 / 應用程式"] = 10,
            ["API"] = 10,
            ["資料庫"] = 25,
            ["日誌"] = 15,
            ["身分驗證"] = 15,
            ["檔案系統"] = 10,
            ["主機資源"] = 5,
            ["安全設定"] = 10,
            ["LLM API"] = 10,
            ["快取服務"] = 10,
            ["AI 計費表"] = 5,
            ["寄信服務"] = 10,
            ["日誌管線"] = 10,
            ["排程作業"] = 5,
        };

        Assert.Equal(14, expected.Count);
        Assert.Equal(150, expected.Values.Sum());

        var source = File.ReadAllText(FindHealthServicePath());

        foreach (var (name, weight) in expected)
        {
            // 每個項目都以 CreateItem("名稱", "分類", 權重, ...) 的形式呼叫，
            // 名稱與權重之間只隔一個分類參數，因此在名稱之後的一小段文字裡找權重即可。
            var nameToken = "\"" + name + "\",";
            var index = source.IndexOf(nameToken, StringComparison.Ordinal);
            Assert.True(index >= 0, $"SystemHealthService 找不到檢查項目「{name}」。");

            var window = source.Substring(index, Math.Min(160, source.Length - index));
            Assert.True(
                window.Contains($"{weight},", StringComparison.Ordinal),
                $"SystemHealthService 的項目「{name}」權重不是 {weight}。"
                + "若確實調整了權重，請一併更新本測試與 docs/features/系統健康監控.md 的權重表。");
        }
    }

    private static string FindHealthServicePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var relative in new[]
            {
                Path.Combine("CustomerHelpAgent.Web", "Health", "SystemHealthService.cs"),
                Path.Combine("src", "CustomerHelpAgent", "CustomerHelpAgent.Web", "Health", "SystemHealthService.cs"),
            })
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("找不到 SystemHealthService.cs。");
    }

    private static SystemHealthItem CreateItem(string name, int weight, SystemHealthStatus status)
    {
        return new SystemHealthItem
        {
            Name = name,
            Category = "Test",
            Weight = weight,
            Status = status,
            Light = SystemHealthScoreCalculator.GetLight(status),
            Evidence = "test"
        };
    }

    private static HealthLogReader CreateReader(string rootPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NLog:BasePath"] = rootPath
            })
            .Build();

        return new HealthLogReader(
            new NLogFilePathResolver(configuration, new TestWebHostEnvironment()),
            NullLogger<HealthLogReader>.Instance);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "CustomerHelpAgent.Web";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
