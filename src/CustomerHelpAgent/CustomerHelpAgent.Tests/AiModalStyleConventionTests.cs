using System.Text.RegularExpressions;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 驗證 AI 分析對話窗的字級一律跟著「一般／中／大」三顆按鈕縮放。
///
/// 0.9.8 起窗內的字級由 CSS 自訂屬性 <c>--ai-font-scale</c>（1／1.25／1.5）控制，
/// 每一條 <c>font-size</c> 都寫成 <c>calc(Npx * var(--ai-font-scale, 1))</c>。
///
/// 之所以需要守門：漏掉一條規則的症狀是「按了放大，只有這一段沒變」——
/// 畫面不會壞、建置不會紅、程式碼看起來也完全正常，只有實際去點那三顆按鈕才發現。
/// 這種漂移最容易在日後補樣式時無聲發生。
///
/// ⚠️ 只檢查<b>會跟著縮放的區塊</b>（meta 資訊表與報告內文）。等待畫面與失敗畫面
/// 刻意用固定字級 —— 那兩個狀態下三顆按鈕根本不存在，沒有「跟著縮放」可言。
/// </summary>
public sealed class AiModalStyleConventionTests
{
    /// <summary>
    /// 各 AI 對話窗的 CSS 檔（相對 CustomerHelpAgent.Web）與其字級會被三顆按鈕縮放的選擇器前綴。
    /// 0.9.68 起多了 AI 例外分析對話窗（meta、報告內文，以及對話泡泡）；
    /// 0.9.72 起多了 AI 對話紀錄明細窗（整窗內容）。
    /// </summary>
    public static TheoryData<string, string[]> ScalableRules => new()
    {
        {
            Path.Combine("Components", "Views", "Analytics", "LogViewerView.razor.css"),
            [".log-ai-meta", ".log-ai-report"]
        },
        {
            Path.Combine("Components", "Views", "Admins", "ExceptionAiAnalysisModal.razor.css"),
            [".exception-ai-meta", ".exception-ai-report", ".exception-ai-entry", ".exception-ai-question", ".exception-ai-error"]
        },
        // 0.9.72 起的 AI 對話紀錄明細窗：窗內每一條 font-size 都要跟著縮放，所以前綴取整個命名空間。
        {
            Path.Combine("Components", "Views", "Analytics", "AiCallLogDetailModal.razor.css"),
            [".ai-call-log-"]
        },
    };

    private static readonly Regex FontSizeDeclaration = new(
        @"font-size\s*:",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Theory]
    [MemberData(nameof(ScalableRules))]
    public void AiModalScalableRules_ShouldMultiplyByFontScaleVariable(string relativeCssPath, string[] scalableSelectorPrefixes)
    {
        var cssPath = Path.Combine(FindWebProjectDirectory(), relativeCssPath);
        var lines = File.ReadAllLines(cssPath);

        // 掃描路徑若失效，測試會空跑綠燈，等於沒有守門。
        Assert.NotEmpty(lines);

        var violations = new List<string>();
        var insideScalableRule = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();

            if (trimmed.StartsWith('}'))
            {
                insideScalableRule = false;
                continue;
            }

            if (scalableSelectorPrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.Ordinal)))
            {
                insideScalableRule = true;
                continue;
            }

            if (insideScalableRule == false || FontSizeDeclaration.IsMatch(trimmed) == false)
            {
                continue;
            }

            if (trimmed.Contains("var(--ai-font-scale", StringComparison.Ordinal) == false)
            {
                violations.Add($"{Path.GetFileName(cssPath)}:{index + 1} {trimmed}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "AI 分析對話窗內的字級必須寫成 calc(Npx * var(--ai-font-scale, 1))，"
                + "否則「一般／中／大」按鈕放大時這一段不會跟著變，而且不會有任何錯誤訊息。"
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    /// <summary>對話窗尺寸的唯一真相來源是 OverlayStyles，不是 &lt;Modal Width&gt;。</summary>
    [Theory]
    [InlineData("Analytics", "LogViewerView.razor")]
    [InlineData("Admins", "ExceptionAiAnalysisModal.razor")]
    [InlineData("Analytics", "AiCallLogDetailModal.razor")]
    public void AiModal_ShouldNotSetWidthOnTheModalTag(string folder, string fileName)
    {
        var razorPath = Path.Combine(FindWebProjectDirectory(), "Components", "Views", folder, fileName);
        var content = File.ReadAllText(razorPath);

        var modalStart = content.IndexOf("<Modal", StringComparison.Ordinal);
        Assert.True(modalStart >= 0, "找不到 AI 分析對話窗的 <Modal> 標籤。");

        var modalEnd = content.IndexOf('>', modalStart);
        var modalTag = content[modalStart..modalEnd];

        Assert.DoesNotContain(
            "Width=",
            modalTag);
    }

    private static string FindWebProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var prefix in new[] { "", Path.Combine("src", "CustomerHelpAgent") })
            {
                var candidate = Path.Combine(dir.FullName, prefix, "CustomerHelpAgent.Web");
                if (File.Exists(Path.Combine(candidate, "CustomerHelpAgent.Web.csproj")))
                {
                    return candidate;
                }
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 CustomerHelpAgent.Web 專案目錄。");
    }
}
