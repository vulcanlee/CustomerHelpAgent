using System.Text;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 匯出檔編碼的守門。
///
/// 起因：三個匯出點（日誌檢視的 <c>.log</c>、系統例外紀錄的 CSV、Token 用量的 CSV）
/// 都寫成 <c>new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(text)</c>，
/// 註解也都寫著「加 BOM」，但 <c>GetBytes</c> **不會**輸出前導碼 ——
/// 那個旗標只影響 <c>GetPreamble()</c> 與 <c>StreamWriter</c>。
///
/// 之所以需要守門：少了 BOM 的症狀是「Excel 開起來整片亂碼」，
/// 而**建置、測試、程式碼審閱全都看不出來** —— 程式碼看起來完全正確，
/// 要實際下載一次、抓出前三個位元組才會發現。0.9.14 修了一處、0.9.16 才補齊另外兩處。
/// </summary>
public sealed class ExportEncodingConventionTests
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    [Fact]
    public void Utf8WithBom_ShouldStartWithBomBytes()
    {
        var bytes = TextDownloadPayload.Utf8WithBom("時間,使用者,作業");

        Assert.True(bytes.Length >= 3);
        Assert.Equal(Utf8Bom, bytes[..3]);
    }

    /// <summary>去掉前導碼之後必須逐字還原，補 BOM 不能動到內容本身。</summary>
    [Fact]
    public void Utf8WithBom_ShouldRoundTripChinese()
    {
        const string text = "最後發生,次數,例外類型\n2026-09-16,3,\"逾時：無法連線\"";

        var bytes = TextDownloadPayload.Utf8WithBom(text);

        Assert.Equal(text, Encoding.UTF8.GetString(bytes[3..]));
    }

    /// <summary>查無資料時仍要下載得到一個編碼正確的空檔，而不是零位元組。</summary>
    [Fact]
    public void Utf8WithBom_ShouldReturnOnlyBom_WhenTextIsEmpty()
    {
        var bytes = TextDownloadPayload.Utf8WithBom(string.Empty);

        Assert.Equal(Utf8Bom, bytes);
    }

    /// <summary>
    /// <c>encoderShouldEmitUTF8Identifier</c> 只能出現在
    /// <see cref="TextDownloadPayload"/> 裡。
    ///
    /// 任何地方再寫一次，幾乎都是同一個誤會（以為它會讓 <c>GetBytes</c> 吐出 BOM）。
    /// 需要含 BOM 的位元組請呼叫 <c>TextDownloadPayload.Utf8WithBom</c>。
    /// </summary>
    [Fact]
    public void WebSources_ShouldNotUseRawUtf8EncodingFlag()
    {
        var webRoot = FindWebProjectRoot();

        var sources = Directory
            .EnumerateFiles(webRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => IsBuildOutput(webRoot, path) == false)
            .ToList();

        // 掃描路徑若失效，測試會空跑綠燈，等於沒有守門。
        Assert.NotEmpty(sources);

        var violations = sources
            .Where(path =>
                string.Equals(Path.GetFileName(path), "TextDownloadPayload.cs", StringComparison.Ordinal) == false)
            .Where(path => File.ReadAllText(path).Contains("encoderShouldEmitUTF8Identifier", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(webRoot, path))
            .ToList();

        Assert.True(
            violations.Count == 0,
            "UTF8Encoding 的 encoderShouldEmitUTF8Identifier 旗標不會讓 GetBytes 輸出 BOM，"
                + "需要含 BOM 的位元組請改用 TextDownloadPayload.Utf8WithBom。"
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    private static bool IsBuildOutput(string webRoot, string path)
    {
        var relative = Path.GetRelativePath(webRoot, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>從測試執行目錄往上找 CustomerHelpAgent.Web 專案目錄（比照 AiModalStyleConventionTests 的作法）。</summary>
    private static string FindWebProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var prefix in new[] { string.Empty, Path.Combine("src", "CustomerHelpAgent") })
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
