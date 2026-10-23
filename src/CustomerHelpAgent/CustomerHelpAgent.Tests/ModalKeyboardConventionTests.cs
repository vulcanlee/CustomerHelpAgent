using System.Text.RegularExpressions;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 驗證 Components 下的 .razor 不在表單（EditForm／form）層攔截鍵盤事件。
///
/// 背景：五個 CRUD 維護對話窗曾把 @onkeydown 掛在 &lt;EditForm&gt; 上，用 Enter 觸發儲存。
/// 但 keydown 會從表單內「任何」子元素冒泡上來，導致 TextArea 無法換行（Shift+Enter 也一樣，
/// 因為判斷式沒看 ShiftKey）、Select 按 Enter 選完選項就關窗、DatePicker／InputNumber／
/// Checkbox／InputFile 全部誤觸存檔關窗 —— 使用者的輸入往往還沒完成。
///
/// AntDesign 的 overlay 攔截幫不上忙：ant-design-blazor.js 的 preventKeyOnCondition 只呼叫
/// preventDefault()，並未 stopPropagation()，事件照樣往上冒泡到 form。
///
/// 正確作法：Enter 交還給元件本身，存檔一律走 &lt;Modal OnOk&gt;（「確定」按鈕），
/// Escape 交給 &lt;Modal Keyboard="true"&gt; —— 這樣下拉／日期面板展開時會先消化掉 Esc，
/// 不會連整個對話窗一起關掉。真的需要鍵盤捷徑時，綁在個別元件上（例如 Input 的 OnPressEnter）。
///
/// 文件只是建議，唯有測試會擋下 PR，因此以此測試守門。
/// </summary>
public sealed class ModalKeyboardConventionTests
{
    /// <summary>
    /// 比對「同一行同時出現表單起始標籤與鍵盤事件屬性」。
    /// 表單層才是問題所在；綁在個別輸入元件上的鍵盤處理不在此限。
    /// </summary>
    private static readonly Regex FormLevelKeyboardHandler = new(
        @"<(EditForm|form)\b[^>]*@onkey(down|up|press)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void RazorComponents_ShouldNotHandleKeyboardEventsOnForms()
    {
        var componentsRoot = FindComponentsRoot();
        var razorFiles = Directory
            .EnumerateFiles(componentsRoot, "*.razor", SearchOption.AllDirectories)
            .ToList();

        // 掃描路徑若失效，測試會空跑綠燈，等於沒有守門。
        Assert.NotEmpty(razorFiles);

        var violations = new List<string>();

        foreach (var file in razorFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                if (FormLevelKeyboardHandler.IsMatch(lines[index]))
                {
                    violations.Add($"{Path.GetFileName(file)}:{index + 1} {lines[index].Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "表單層（EditForm／form）不得攔截鍵盤事件：keydown 會從 TextArea／Select／DatePicker／"
                + "Checkbox 等子元素冒泡上來，造成「輸入還沒完成就存檔關窗」。"
                + "存檔請走 <Modal OnOk>，Esc 交給 <Modal Keyboard=\"true\">；"
                + "真的需要捷徑請綁在個別元件上（例如 Input 的 OnPressEnter）。"
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
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
