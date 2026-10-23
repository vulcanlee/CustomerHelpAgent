using System.Text.Json;
using Microsoft.Extensions.Configuration;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 擋住一類「防線存在但不生效」的錯誤。
///
/// <see cref="StartupSafetyValidator"/> 是拿**寫死的字面值**去比對範本出貨的設定值。
/// 只要有人換了範本的預設值而忘了同步比對清單，整道 Production 防線就靜默失效 ——
/// 建置不會紅、測試不會紅、部署也不會被擋，只有真的出事才會知道。
///
/// 歷史：0.9.47 查到驗證器只擋字面 <c>"support"</c>，但範本從某一版起出貨的是
/// <c>"1qaz@WSX"</c>，那道防線已形同虛設很久；同期也查到 JWT 佔位金鑰因為
/// <c>New-StarterProject.ps1</c> 會換前綴，精確比對在衍生專案裡一樣失效。
/// 0.9.57 起出貨值統一為 <c>"support"</c>，<c>"1qaz@WSX"</c> 已自比對清單移除。
///
/// ⚠️ 這兩條測試刻意**讀 <c>appsettings.json</c> 實際出貨的值**，而不是把值再寫死一次 ——
/// 否則就只是把同一個錯誤複製到測試裡，什麼也擋不住。
/// </summary>
public sealed class StartupSafetyConventionTests
{
    [Fact]
    public void ShippedSigningKey_ShouldBeRejectedInProduction()
    {
        var shipped = ReadShippedValue("JwtSettings", "SigningKey");
        var configuration = BuildProductionSafeConfiguration("JwtSettings:SigningKey", shipped);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StartupSafetyValidator.Validate(configuration, "Production"));

        Assert.Contains("JwtSettings:SigningKey", exception.Message);
    }

    [Fact]
    public void ShippedSupportPassword_ShouldBeRejectedInProduction()
    {
        var shipped = ReadShippedValue("BootstrapSettings", "SupportPassword");
        var configuration = BuildProductionSafeConfiguration("BootstrapSettings:SupportPassword", shipped);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StartupSafetyValidator.Validate(configuration, "Production"));

        Assert.Contains("BootstrapSettings:SupportPassword", exception.Message);
    }

    /// <summary>讀 <c>appsettings.json</c> 裡某個區段的某個鍵，讀不到就讓測試失敗而不是空跑綠燈。</summary>
    private static string ReadShippedValue(string section, string key)
    {
        var path = Path.Combine(FindWebRoot(), "appsettings.json");
        Assert.True(File.Exists(path), $"找不到 {path}。");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(
            document.RootElement.TryGetProperty(section, out var sectionElement),
            $"appsettings.json 沒有 {section} 區段。區段若改名，請一併更新這條測試。");
        Assert.True(
            sectionElement.TryGetProperty(key, out var valueElement),
            $"appsettings.json 的 {section} 沒有 {key}。鍵若改名，請一併更新這條測試。");

        return valueElement.GetString() ?? string.Empty;
    }

    /// <summary>除了受測的那一個鍵之外，其餘都給合格值，確保錯誤訊息只會來自受測項目。</summary>
    private static IConfiguration BuildProductionSafeConfiguration(string key, string value)
    {
        var settings = new Dictionary<string, string?>
        {
            ["JwtSettings:SigningKey"] = "ProductionSigningKey-AtLeast32CharactersLong-ok",
            ["BootstrapSettings:SupportAccount"] = "support",
            ["BootstrapSettings:SupportPassword"] = "a-real-password",
            ["Swagger:EnabledInProduction"] = "false",
            [key] = value,
        };

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static string FindWebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "CustomerHelpAgent.Web");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "CustomerHelpAgent", "CustomerHelpAgent.Web");
            if (Directory.Exists(srcCandidate))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 CustomerHelpAgent.Web。");
    }
}
