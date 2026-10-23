using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AntDesign;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 右下角通知（0.9.129 起）：錯誤與真正的警告附「複製」按鈕、不自動關閉，並在顯示當下寫一筆日誌；
/// 成功訊息（沿用 Warning）與 Info 維持原樣。
///
/// 放進 <see cref="IntegrationHostCollection"/>：<see cref="ViewNotification"/> 的 logger 是行程層級的靜態欄位，
/// 啟動真實 host 的測試會把它換掉，平行執行時這裡收到的日誌就不準。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class ViewNotificationTests : ComponentTestBase
{
    private static readonly DateTime OccurredAt = new(2026, 10, 19, 14, 3, 22);

    public ViewNotificationTests()
        => ToastCopyButtonTests.AddCopyServices(Services);

    [Fact]
    public void CopyText_ShouldListTitleTextTraceCodeTimeAndPage()
    {
        var text = ToastCopyText.Build("系統訊息", "名稱重複", "ABCD1234", OccurredAt, "https://host/Categories");

        Assert.Equal(
            string.Join(Environment.NewLine,
                "系統訊息",
                "名稱重複",
                "錯誤追蹤碼：ABCD1234",
                "時間：2026/10/19 14:03:22",
                "頁面：https://host/Categories"),
            text);
    }

    [Fact]
    public void CopyText_WithoutTraceCode_ShouldOmitThatLine()
    {
        var text = ToastCopyText.Build("系統訊息", "名稱重複", null, OccurredAt, "https://host/");

        Assert.DoesNotContain("錯誤追蹤碼", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyableConfig_ShouldNotAutoClose_AndRenderACopyButton()
    {
        var config = ViewNotification.BuildCopyableConfig("系統訊息", "名稱重複", NotificationType.Error, "ABCD1234", OccurredAt, showTraceCode: false);

        Assert.Equal(0, config.Duration);
        Assert.NotNull(config.Btn);
        Assert.Equal("複製", Render(config.Btn).Find("button.toast-copy-button").TextContent.Trim());
    }

    [Fact]
    public void CopyableConfig_ShowingTraceCode_ShouldAppendItToTheScreenTextOnly()
    {
        var config = ViewNotification.BuildCopyableConfig("系統訊息", "匯出失敗", NotificationType.Error, "ABCD1234", OccurredAt, showTraceCode: true);

        Assert.Equal("匯出失敗（錯誤追蹤碼：ABCD1234）", config.Description.AsT0);

        // 複製按鈕拿到的是原始內文，追蹤碼另起一行，不會出現兩次。
        var button = Render(config.Btn).FindComponent<ToastCopyButton>();
        Assert.Equal("匯出失敗", button.Instance.Description);
        Assert.Equal("ABCD1234", button.Instance.TraceCode);
    }

    [Fact]
    public void CopyText_WithDetail_ShouldAppendItAsTheLastBlock()
    {
        var text = ToastCopyText.Build("系統訊息", "匯出失敗", "ABCD1234", OccurredAt, "https://host/", "System.InvalidOperationException: boom");

        Assert.EndsWith(
            string.Join(Environment.NewLine,
                "頁面：https://host/",
                "",
                "----- 錯誤詳細資料開始 -----",
                "System.InvalidOperationException: boom",
                "----- 錯誤詳細資料結束 -----"),
            text);
    }

    [Fact]
    public void UnexpectedError_ShouldHandTheWholeExceptionToTheButton_ButNotToTheScreenOrTheLog()
    {
        var factory = new CapturingLoggerFactory();
        var notificationService = Services.GetRequiredService<NotificationService>();
        var container = Render<AntContainer>();
        Exception exception;
        try
        {
            throw new InvalidOperationException("boom", new TimeoutException("inner"));
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        ViewNotification.UseLoggerFactory(factory);
        try
        {
            ViewNotification.UnexpectedError(notificationService, "匯出失敗", exception);
        }
        finally
        {
            ViewNotification.UseLoggerFactory(NullLoggerFactory.Instance);
        }

        var button = container.WaitForComponent<ToastCopyButton>();
        Assert.Equal(exception.ToString(), button.Instance.Detail);
        Assert.Contains("System.TimeoutException: inner", button.Instance.Detail, StringComparison.Ordinal);
        Assert.Equal("匯出失敗", button.Instance.Description);
        Assert.DoesNotContain("InvalidOperationException", container.Find(".ant-notification-notice-description").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", Assert.Single(factory.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void PlainConfig_ShouldKeepTheDefaultDuration_AndHaveNoButton()
    {
        var config = ViewNotification.BuildConfig("系統訊息", "新增成功", NotificationType.Warning);

        Assert.Null(config.Duration);
        Assert.Null(config.Btn);
    }

    [Fact]
    public void ShowingNotifications_ShouldLogErrorsAndCautions_ButNotSuccessOrInfo()
    {
        var factory = new CapturingLoggerFactory();
        var notificationService = Services.GetRequiredService<NotificationService>();
        ViewNotification.UseLoggerFactory(factory);
        try
        {
            ViewNotification.Warning(notificationService, "新增成功");
            ViewNotification.Info(notificationService, "正在送出");
            ViewNotification.Error(notificationService, "名稱重複");
            ViewNotification.UnexpectedError(notificationService, "匯出失敗", new InvalidOperationException("boom"));
            ViewNotification.ValidationError(notificationService, "請填寫名稱");
            ViewNotification.Caution(notificationService, "備份已經在執行中");
        }
        finally
        {
            ViewNotification.UseLoggerFactory(NullLoggerFactory.Instance);
        }

        Assert.Equal(
            [
                (LogLevel.Warning, "名稱重複"),
                (LogLevel.Error, "匯出失敗"),
                (LogLevel.Warning, "請填寫名稱"),
                (LogLevel.Warning, "備份已經在執行中"),
            ],
            factory.Entries.Select(entry => (entry.Level, entry.Text)).ToArray());
    }

    /// <summary>
    /// 繞過 <see cref="ViewNotification"/> 自己開通知，就沒有複製按鈕也不會寫日誌
    /// （0.9.129 之前變更密碼頁就是這樣）。
    /// </summary>
    [Fact]
    public void Notifications_ShouldOnlyBeOpenedThroughViewNotification()
    {
        var directOpen = new Regex(@"new\s+NotificationConfig\b|[Nn]otificationService\.Open\(", RegexOptions.Compiled);
        var webRoot = Path.Combine(FindSourceRoot(), "CustomerHelpAgent.Web");

        var violations = Directory.EnumerateFiles(webRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".razor", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => Path.GetFileName(path) != "ViewNotification.cs")
            .Where(path => directOpen.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(webRoot, path))
            .ToList();

        Assert.True(violations.Count == 0,
            "右下角通知一律透過 ViewNotification 開啟（才有複製按鈕與日誌）："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "CustomerHelpAgent.Web");
            if (Directory.Exists(candidate))
            {
                return dir.FullName;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "CustomerHelpAgent");
            if (Directory.Exists(Path.Combine(srcCandidate, "CustomerHelpAgent.Web")))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 src/CustomerHelpAgent。");
    }

    /// <summary>記下每一筆日誌的等級與通知內文（結構化參數 NotificationText）。</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory, ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Entries { get; } = new();

        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.FirstOrDefault(pair => pair.Key == "NotificationText").Value as string
                : null;
            Entries.Enqueue((logLevel, text ?? formatter(state, exception)));
            Messages.Enqueue(formatter(state, exception));
        }
    }
}
