using CustomerHelpAgent.Business.Helpers;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 上傳檔案類型政策。這是安全行為，不是格式偏好：
///
/// - 儲存檔名雖已改為 GUID（無路徑穿越），但**副檔名會保留**，下載時也會回吐 ContentType。
///   允許 .html / .svg 等同在自己的網域上開一個 stored XSS。
/// - 呼叫端提供的 ContentType 完全不可信（可上傳 .txt 卻宣稱 text/html），
///   因此一律依副檔名決定。
/// </summary>
public sealed class UploadFileTypePolicyTests
{
    [Theory]
    [InlineData("報表.pdf")]
    [InlineData("清單.xlsx")]
    [InlineData("照片.PNG")]      // 大小寫不敏感
    [InlineData("封存.zip")]
    public void IsAllowed_WithPermittedExtension_ShouldReturnTrue(string fileName)
    {
        Assert.True(UploadFileTypePolicy.IsAllowed(fileName));
    }

    [Theory]
    [InlineData("payload.html")]
    [InlineData("payload.htm")]
    [InlineData("payload.svg")]   // 可內嵌 script
    [InlineData("payload.xhtml")]
    [InlineData("payload.js")]
    [InlineData("payload.exe")]
    [InlineData("payload.bat")]
    [InlineData("payload.ps1")]
    public void IsAllowed_WithDangerousExtension_ShouldReturnFalse(string fileName)
    {
        Assert.False(UploadFileTypePolicy.IsAllowed(fileName));
    }

    [Theory]
    [InlineData("沒有副檔名")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAllowed_WithoutExtension_ShouldReturnFalse(string? fileName)
    {
        Assert.False(UploadFileTypePolicy.IsAllowed(fileName));
    }

    [Fact]
    public void IsAllowed_WithCustomAllowList_ShouldOverrideDefaults()
    {
        string[] custom = [".dwg", "txt"];   // 可帶或不帶前導點

        Assert.True(UploadFileTypePolicy.IsAllowed("圖面.dwg", custom));
        Assert.True(UploadFileTypePolicy.IsAllowed("說明.txt", custom));

        // 自訂清單沒列的，即使在預設清單內也要擋下。
        Assert.False(UploadFileTypePolicy.IsAllowed("報表.pdf", custom));
    }

    [Fact]
    public void IsAllowed_WithEmptyAllowList_ShouldFallBackToDefaults()
    {
        Assert.True(UploadFileTypePolicy.IsAllowed("報表.pdf", []));
        Assert.False(UploadFileTypePolicy.IsAllowed("payload.html", []));
    }

    [Theory]
    [InlineData("報表.pdf", "application/pdf")]
    [InlineData("清單.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("照片.PNG", "image/png")]
    [InlineData("說明.txt", "text/plain")]
    public void ResolveContentType_ShouldMapByExtension(string fileName, string expected)
    {
        Assert.Equal(expected, UploadFileTypePolicy.ResolveContentType(fileName));
    }

    /// <summary>
    /// 白名單被覆寫成預設清單沒有的副檔名時，回退為 octet-stream ——
    /// 瀏覽器不會內嵌執行，是安全的預設。
    /// </summary>
    [Theory]
    [InlineData("圖面.dwg")]
    [InlineData("沒有副檔名")]
    public void ResolveContentType_WithUnknownExtension_ShouldFallBackToOctetStream(string fileName)
    {
        Assert.Equal(UploadFileTypePolicy.DefaultContentType, UploadFileTypePolicy.ResolveContentType(fileName));
    }

    /// <summary>預設白名單不得包含任何可執行 script 的類型。</summary>
    [Fact]
    public void DefaultAllowedExtensions_ShouldNotContainScriptableTypes()
    {
        string[] forbidden = [".html", ".htm", ".svg", ".xhtml", ".js", ".exe", ".dll", ".bat", ".cmd", ".ps1", ".sh"];

        foreach (var extension in forbidden)
        {
            Assert.DoesNotContain(
                extension,
                UploadFileTypePolicy.DefaultAllowedExtensions,
                StringComparer.OrdinalIgnoreCase);
        }
    }
}
