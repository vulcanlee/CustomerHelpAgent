using System.Text.RegularExpressions;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 申請管道（M5）：多 Agent 建立的申請用 <see cref="AfterSalesChannel.AgentHandoff"/>（「AI 多 Agent」），
/// 與單一 Agent 一樣算 AI 管道——M4 的初審、審核頁 AI 面板與客服 AI 助手一律以 <see cref="AfterSalesChannelExtensions.IsAi"/> 判斷。
/// </summary>
public sealed class AfterSalesChannelTests
{
    [Fact]
    public void IsAi_TrueForAgentAndHandoff()
    {
        Assert.True(AfterSalesChannel.Agent.IsAi());
        Assert.True(AfterSalesChannel.AgentHandoff.IsAi());
        Assert.False(AfterSalesChannel.Traditional.IsAi());
        Assert.Equal([AfterSalesChannel.Agent, AfterSalesChannel.AgentHandoff], AfterSalesChannelExtensions.AiChannels);
    }

    [Fact]
    public void ChannelText_HandoffIsMultiAgent()
    {
        Assert.Equal("AI 多 Agent", AfterSalesLabels.ChannelText(AfterSalesChannel.AgentHandoff));
        Assert.Equal("AI 助理", AfterSalesLabels.ChannelText(AfterSalesChannel.Agent));
        Assert.Equal("傳統表單", AfterSalesLabels.ChannelText(AfterSalesChannel.Traditional));
    }

    [Fact]
    public void ForAgentMode_MapsSessionModeToChannel()
    {
        Assert.Equal(AfterSalesChannel.Agent, AfterSalesChannelExtensions.ForAgentMode(AgentMode.Single));
        Assert.Equal(AfterSalesChannel.AgentHandoff, AfterSalesChannelExtensions.ForAgentMode(AgentMode.Handoff));
    }

    /// <summary>線上客服的 <c>?mode=</c> 網址參數（展示控制台產生、線上客服頁解析）。</summary>
    [Fact]
    public void AgentModeQuery_RoundTrips()
    {
        Assert.Equal("mode", AgentModeLabels.QueryName);
        Assert.Equal("handoff", AgentModeLabels.QueryValue(AgentMode.Handoff));
        Assert.Equal("single", AgentModeLabels.QueryValue(AgentMode.Single));
        Assert.Equal(AgentMode.Handoff, AgentModeLabels.FromQuery("handoff"));
        Assert.Equal(AgentMode.Handoff, AgentModeLabels.FromQuery(" Handoff "));
        Assert.Equal(AgentMode.Single, AgentModeLabels.FromQuery("single"));
        Assert.Null(AgentModeLabels.FromQuery(null));
        Assert.Null(AgentModeLabels.FromQuery("multi"));
        Assert.Equal("多 Agent", AgentModeLabels.Text(AgentMode.Handoff));
        Assert.Equal("單一 Agent", AgentModeLabels.Text(AgentMode.Single));
    }

    /// <summary>
    /// 只比對單一 Agent 管道的寫法：<c>==</c>／<c>!=</c>、<c>is</c>／<c>is not</c>、模式組合的 <c>or</c>／<c>and</c>、<c>case</c>，
    /// 以及 switch 運算式的分支（<c>AfterSalesChannel.Agent =&gt;</c>）。<c>AgentHandoff</c> 不算。
    /// </summary>
    private static readonly Regex AgentChannelComparison = new(
        @"(\bis(\s+not)?\s+|[!=]=\s*|\bcase\s+|\b(or|and)\s+)AfterSalesChannel\.Agent\b(?!Handoff)|AfterSalesChannel\.Agent\b(?!Handoff)\s*=>");

    /// <summary>
    /// 合法逐一列舉各管道的地方：判斷本身（<c>IsAi</c>）與中文標籤。設定預設值（<c>= AfterSalesChannel.Agent</c>）不是比較，不會被比對到。
    /// </summary>
    private static readonly string[] AllowedFiles = ["AfterSalesChannelExtensions.cs", "AfterSalesLabels.cs"];

    [Theory]
    [InlineData("if (channel == AfterSalesChannel.Agent)")]
    [InlineData("x.Channel != AfterSalesChannel.Agent")]
    [InlineData("if (channel is AfterSalesChannel.Agent)")]
    [InlineData("if (channel is not AfterSalesChannel.Agent)")]
    [InlineData("channel is AfterSalesChannel.Traditional or AfterSalesChannel.Agent")]
    [InlineData("case AfterSalesChannel.Agent:")]
    [InlineData("AfterSalesChannel.Agent => true,")]
    public void AgentChannelComparison_CatchesComparisonForms(string code)
        => Assert.Matches(AgentChannelComparison, code);

    [Theory]
    [InlineData("if (channel == AfterSalesChannel.AgentHandoff)")]
    [InlineData("case AfterSalesChannel.AgentHandoff:")]
    [InlineData("Channel = AfterSalesChannel.Agent,")]
    [InlineData("public AfterSalesChannel Channel { get; init; } = AfterSalesChannel.Agent;")]
    [InlineData("mode == AgentMode.Handoff ? AfterSalesChannel.AgentHandoff : AfterSalesChannel.Agent")]
    [InlineData("if (channel.IsAi())")]
    public void AgentChannelComparison_IgnoresNonComparisons(string code)
        => Assert.DoesNotMatch(AgentChannelComparison, code);

    /// <summary>
    /// 守門：產品程式不再只比對單一 Agent 管道（只認單一 Agent 會讓多 Agent 的申請漏掉初審與 AI 面板）；
    /// 一律用 <c>IsAi()</c>，EF 查詢用 <c>AiChannels.Contains(...)</c>。例外只有 <see cref="AllowedFiles"/>。
    /// </summary>
    [Fact]
    public void NoDirectAgentChannelComparisons()
    {
        var offenders = EnumerateSourceFiles()
            .Where(path => !AllowedFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .SelectMany(path => File.ReadAllLines(path)
                .Select((line, index) => (line, index))
                .Where(x => AgentChannelComparison.IsMatch(x.line))
                .Select(x => $"{Path.GetFileName(path)}:{x.index + 1}: {x.line.Trim()}"))
            .ToList();

        Assert.Empty(offenders);
    }

    private static IEnumerable<string> EnumerateSourceFiles()
    {
        var root = FindSourceRoot();
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !path.Contains("CustomerHelpAgent.Tests"));
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var srcCandidate = Path.Combine(dir.FullName, "src", "CustomerHelpAgent");
            if (Directory.Exists(Path.Combine(srcCandidate, "CustomerHelpAgent.Web")))
            {
                return srcCandidate;
            }

            if (Directory.Exists(Path.Combine(dir.FullName, "CustomerHelpAgent.Web")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 src/CustomerHelpAgent。");
    }
}
