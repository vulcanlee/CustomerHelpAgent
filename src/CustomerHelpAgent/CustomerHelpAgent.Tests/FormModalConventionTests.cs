using System.Text.RegularExpressions;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 驗證「大量資料輸入對話窗」的共用骨架沒有被繞過。
///
/// 這幾條規範的失敗症狀都是**靜默的** —— 不會壞、不會紅、只有打開那一頁才看得出來，
/// 所以文件擋不住漂移，只有測試會擋下 PR：
///
/// - 少了 <c>form-modal</c>：窗退回 AntDesign 預設的 520px 小窗、body 不捲動。
///   0.9.24 之前 <c>category-view-modal</c> 與 <c>team-view-modal</c> 就是這樣漏掉尺寸規則的
///   —— class 掛在 razor 上，但 OverlayStyles 裡從來沒有對應規則。
/// - 少了 <c>MaskClosable="false"</c>：使用者誤點遮罩，整份輸入無聲蒸發。
/// - 用了 <c>Width</c>：尺寸有兩個來源，下一個人必定改錯地方（見速查表 §6.4）。
///
/// 規範全文見 docs/architecture/對話窗 UI 設計規範.md。
/// </summary>
public sealed class FormModalConventionTests
{
    /// <summary>
    /// 尚未遷移到共用骨架的既有檢視。
    ///
    /// 這份清單只能縮短，不能加長：新的表單對話窗一律要照規範寫。
    /// 名單上的檢視一旦遷移完成，<see cref="MigrationAllowList_ShouldNotContainAlreadyMigratedViews"/>
    /// 會要求你把它從這裡刪掉，避免名單放著爛掉、變成永久豁免。
    ///
    /// 0.9.27 起全部遷移完成，清單已清空 —— 新增項目前請先確認那真的是暫時的例外。
    /// </summary>
    private static readonly string[] PendingMigrationViews = [];

    private static readonly Regex ModalOpenTag = new(@"<Modal\b[^>]*>", RegexOptions.Compiled);
    private static readonly Regex ClassAttribute = new(@"Class=""(?<value>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex ModalConfirmCall = new(@"(?<!Runner\.)ConfirmAsync\(", RegexOptions.Compiled);

    /// <summary>
    /// 含 <c>&lt;EditForm&gt;</c> 的對話窗＝表單型對話窗，必須套用共用骨架。
    /// 唯讀明細窗（例外紀錄、Token 用量、AI 分析）不在此限。
    /// </summary>
    [Fact]
    public void FormModals_ShouldUseTheSharedSkeleton()
    {
        var violations = new List<string>();

        foreach (var (file, openTag, body) in EnumerateModals())
        {
            if (body.Contains("<EditForm", StringComparison.Ordinal) == false)
            {
                continue;
            }

            var name = Path.GetFileName(file);
            if (PendingMigrationViews.Contains(name))
            {
                continue;
            }

            if (openTag.Contains("form-modal", StringComparison.Ordinal) == false)
            {
                violations.Add($"{name}：<Modal Class> 必須包含 form-modal —— 尺寸與 2 欄版型的唯一來源。");
            }

            if (openTag.Contains("Width=", StringComparison.Ordinal))
            {
                violations.Add($"{name}：尺寸一律寫在 OverlayStyles.razor，不得使用 <Modal Width>（速查表 §6.4）。");
            }

            if (openTag.Contains(@"MaskClosable=""false""", StringComparison.Ordinal) == false)
            {
                violations.Add($"{name}：必須明寫 MaskClosable=\"false\" —— 大量輸入的窗，誤點遮罩會無聲丟掉整份輸入。");
            }

            if (openTag.Contains("OnCancel=", StringComparison.Ordinal) == false)
            {
                violations.Add($"{name}：必須掛 OnCancel —— ✕、取消鈕與 ESC 三個入口都由它接住未儲存確認。");
            }

            if (openTag.Contains(@"Keyboard=""true""", StringComparison.Ordinal) == false)
            {
                violations.Add($"{name}：必須明寫 Keyboard=\"true\"，Escape 交給 Modal 處理（速查表 §6.3）。");
            }
        }

        Assert.True(
            violations.Count == 0,
            "表單對話窗未套用共用骨架，詳見 docs/architecture/對話窗 UI 設計規範.md："
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 每個用到的 <c>*-modal</c> class，OverlayStyles.razor 裡都要有對應規則，
    /// 否則那個窗會安靜地退回 AntDesign 預設尺寸。
    /// </summary>
    [Fact]
    public void EveryModalClass_ShouldHaveRulesInOverlayStyles()
    {
        var helper = File.ReadAllText(Path.Combine(FindComponentsRoot(), "Commons", "OverlayStyles.razor"));
        var violations = new List<string>();

        foreach (var (file, openTag, _) in EnumerateModals())
        {
            var name = Path.GetFileName(file);
            var match = ClassAttribute.Match(openTag);

            if (match.Success == false)
            {
                violations.Add($"{name}：<Modal> 沒有 Class，尺寸規則無處可掛。");
                continue;
            }

            foreach (var token in match.Groups["value"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.EndsWith("-modal", StringComparison.Ordinal) == false)
                {
                    continue;
                }

                if (helper.Contains("." + token, StringComparison.Ordinal) == false)
                {
                    violations.Add($"{name}：.{token} 在 OverlayStyles.razor 找不到任何規則。");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "對話窗的 class 在 OverlayStyles.razor 沒有對應規則："
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 禁止舊寫法「每條失敗路徑各補一次 <c>modalVisible = true;</c>」。
    ///
    /// AntDesign 在呼叫 OnOk／OnCancel 之前就已送出 VisibleChanged(false)，
    /// 舊作法是在每個早退分支各補一次把窗撐回去（一個檢視重複四次）；
    /// 只要新增一條早退路徑而忘了補，症狀就是「按儲存 → 驗證失敗 → 窗關了 → 輸入全丟」。
    /// 正確作法是 handler 第一行搶回 Visible，失敗路徑只要 return false（見 FormModalFlow）。
    /// </summary>
    [Fact]
    public void ViewCodeBehind_ShouldNotReopenModalOnEachFailurePath()
    {
        var componentsRoot = FindComponentsRoot();
        var files = Directory.EnumerateFiles(componentsRoot, "*.razor.cs", SearchOption.AllDirectories).ToList();

        Assert.NotEmpty(files);

        var pattern = new Regex(@"modalVisible\s*=\s*true;\s*(?://[^\n]*\n\s*)*return;", RegexOptions.Compiled);
        var violations = new List<string>();

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (PendingMigrationViews.Any(view => name == view + ".cs"))
            {
                continue;
            }

            if (pattern.IsMatch(File.ReadAllText(file)))
            {
                violations.Add(name);
            }
        }

        Assert.True(
            violations.Count == 0,
            "失敗路徑不要各自把 Modal 撐回去：handler 第一行搶回 Visible，失敗路徑 return false 即可"
                + "（見 Components/Commons/FormModalFlow.cs）。"
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 確認窗一律走 <c>Components/Commons/ConfirmDialog.cs</c> 樣板，各檢視不得自己寫
    /// <c>ConfirmAsync</c>。
    ///
    /// 這不只是文案一致的問題：自己寫必定會漏參數。0.9.25 之前，例外紀錄與 Token 用量
    /// 的六個「刪除／清空」全都少了 <c>OkButtonProps.Danger</c> 與 <c>MaskClosable = false</c>
    /// —— 長得像一般提醒，而且**誤點遮罩就直接執行了不可復原的動作**。
    /// 視覺上的紅調也是靠 Danger 當 CSS hook，少設一次那個窗就不會轉紅。
    /// </summary>
    [Fact]
    public void ConfirmDialogs_ShouldOnlyBeCreatedByTheSharedTemplate()
    {
        var componentsRoot = FindComponentsRoot();
        var commonsRoot = Path.Combine(componentsRoot, "Commons") + Path.DirectorySeparatorChar;
        var files = Directory
            .EnumerateFiles(componentsRoot, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(componentsRoot, "*.razor", SearchOption.AllDirectories))
            .ToList();

        Assert.NotEmpty(files);

        var violations = new List<string>();

        foreach (var file in files)
        {
            if (file.StartsWith(commonsRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // AfterSalesAgentRunner.ConfirmAsync 是售後 Agent 的「顧客按下確認卡」，不是確認窗（1.0.4 起，線上客服頁）。
            if (ModalConfirmCall.IsMatch(File.ReadAllText(file)))
            {
                violations.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            violations.Count == 0,
            "確認窗請走 ConfirmDialog.AskDestructiveAsync／AskAsync／AskDeleteRecordAsync，"
                + "不要自己寫 ConfirmAsync —— 自己寫必定會漏掉 Danger／MaskClosable／ZIndex。"
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 浮層的全域樣式只能在 <c>Routes.razor</c> 渲染一次。
    ///
    /// AntDesign 把對話窗、確認窗、通知與消息條全部渲染在 <c>AntContainer</c> 底下，與 layout 無關。
    /// 0.9.25 之前這支元件散在九個檢視與 MainLayout 裡，結果用別的 layout 的頁面
    /// （登入頁的 NoFooterLayout、首頁的 EmptyLayout）整組吃不到樣式，
    /// 而那種缺失只有打開那一頁才看得出來。
    /// </summary>
    [Fact]
    public void OverlayStyles_ShouldBeRenderedExactlyOnceInRoutes()
    {
        var componentsRoot = FindComponentsRoot();
        var razorFiles = Directory.EnumerateFiles(componentsRoot, "*.razor", SearchOption.AllDirectories).ToList();

        Assert.NotEmpty(razorFiles);

        var renderSites = new List<string>();

        foreach (var file in razorFiles)
        {
            var name = Path.GetFileName(file);

            // 元件自己的定義檔不算渲染點。
            if (string.Equals(name, "OverlayStyles.razor", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            for (var index = text.IndexOf("<OverlayStyles", StringComparison.Ordinal);
                 index >= 0;
                 index = text.IndexOf("<OverlayStyles", index + 1, StringComparison.Ordinal))
            {
                renderSites.Add(name);
            }
        }

        Assert.True(
            renderSites.Count == 1 && renderSites[0] == "Routes.razor",
            "OverlayStyles 只能在 Routes.razor 渲染一次（AntContainer 旁邊）——"
                + "掛在 layout 或各檢視裡，只要有頁面用了別的 layout 就會吃不到樣式。"
                + Environment.NewLine
                + "實際渲染點："
                + (renderSites.Count == 0 ? "（無）" : string.Join("、", renderSites)));
    }

    /// <summary>
    /// AntContainer（對話窗、確認窗、通知、消息條）在頁面的錯誤邊界之外。
    /// 0.9.77 之前它沒有任何邊界：裡面一拋未處理例外，整個 circuit 就中斷（畫面突然沒反應），
    /// 只留下一行框架日誌（LOG-07）。Routes.razor 必須用 LoggingErrorBoundary 包住它。
    /// </summary>
    [Fact]
    public void AntContainer_ShouldBeWrappedInErrorBoundary()
    {
        var text = File.ReadAllText(Path.Combine(FindComponentsRoot(), "Routes.razor"));

        var containerIndex = text.IndexOf("<AntContainer", StringComparison.Ordinal);
        Assert.True(containerIndex >= 0, "Routes.razor 找不到 <AntContainer />。");

        var boundaryOpen = text.LastIndexOf("<LoggingErrorBoundary", containerIndex, StringComparison.Ordinal);
        var boundaryCloseBefore = text.LastIndexOf("</LoggingErrorBoundary>", containerIndex, StringComparison.Ordinal);
        var boundaryCloseAfter = text.IndexOf("</LoggingErrorBoundary>", containerIndex, StringComparison.Ordinal);

        Assert.True(
            boundaryOpen >= 0 && boundaryOpen > boundaryCloseBefore && boundaryCloseAfter > containerIndex,
            "Routes.razor 的 <AntContainer /> 必須包在 <LoggingErrorBoundary> 之內，"
                + "否則浮層內的未處理例外會讓整個 circuit 中斷。");
    }

    /// <summary>
    /// 待遷移清單不得放著爛掉：名單上的檢視若已經套用共用骨架，就要把它從名單刪掉。
    /// </summary>
    [Fact]
    public void MigrationAllowList_ShouldNotContainAlreadyMigratedViews()
    {
        var stale = new List<string>();

        foreach (var (file, openTag, body) in EnumerateModals())
        {
            var name = Path.GetFileName(file);

            if (PendingMigrationViews.Contains(name) == false)
            {
                continue;
            }

            if (body.Contains("<EditForm", StringComparison.Ordinal)
                && openTag.Contains("form-modal", StringComparison.Ordinal))
            {
                stale.Add(name);
            }
        }

        Assert.True(
            stale.Count == 0,
            "這些檢視已經遷移完成，請從 PendingMigrationViews 移除，讓守門規則開始涵蓋它們："
                + Environment.NewLine
                + string.Join(Environment.NewLine, stale));
    }

    /// <summary>
    /// 果凍樣式必須寫在 <c>.ant-modal .ant-modal-content</c> 這個共用基底上。
    ///
    /// 0.9.25／0.9.26 是用「逐一為 .form-modal 與 .ant-modal-confirm 各寫一份」的加法做法，
    /// 結果關於、變更密碼與三個唯讀明細窗整組被漏掉 —— 沒被點名的窗自動落空，
    /// 而且下一個新增的窗也會再落空一次。0.9.28 改成共用基底根治。
    ///
    /// 這條測試擋的是「有人把基底拆掉、退回逐一加法」。真正防止漏窗的是**結構**，
    /// 測試只負責不讓結構被拆。
    /// </summary>
    [Fact]
    public void JellyStyles_ShouldLiveOnTheSharedModalBase()
    {
        var overlayStyles = File.ReadAllText(
            Path.Combine(FindComponentsRoot(), "Commons", "OverlayStyles.razor"));

        Assert.Contains(".ant-modal .ant-modal-content", overlayStyles, StringComparison.Ordinal);
        Assert.Contains("@keyframes ov-arrive", overlayStyles, StringComparison.Ordinal);
    }

    /// <summary>掃出所有 &lt;Modal&gt;，回傳（檔案、開始標籤、標籤到 &lt;/Modal&gt; 之間的內容）。</summary>
    private static IEnumerable<(string File, string OpenTag, string Body)> EnumerateModals()
    {
        var componentsRoot = FindComponentsRoot();
        var razorFiles = Directory.EnumerateFiles(componentsRoot, "*.razor", SearchOption.AllDirectories).ToList();

        // 掃描路徑若失效，測試會空跑綠燈，等於沒有守門。
        Assert.NotEmpty(razorFiles);

        var results = new List<(string, string, string)>();

        foreach (var file in razorFiles)
        {
            var text = File.ReadAllText(file);

            foreach (Match match in ModalOpenTag.Matches(text))
            {
                var bodyStart = match.Index + match.Length;
                var bodyEnd = text.IndexOf("</Modal>", bodyStart, StringComparison.Ordinal);
                var body = bodyEnd < 0 ? string.Empty : text[bodyStart..bodyEnd];

                results.Add((file, match.Value, body));
            }
        }

        Assert.NotEmpty(results);

        return results;
    }

    private static string FindComponentsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "CustomerHelpAgent.Web", "Components");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "CustomerHelpAgent", "CustomerHelpAgent.Web", "Components");
            if (Directory.Exists(srcCandidate))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 CustomerHelpAgent.Web/Components。");
    }
}
