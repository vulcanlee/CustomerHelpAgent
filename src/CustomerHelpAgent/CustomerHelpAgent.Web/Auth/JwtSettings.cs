using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Web.Auth;

public class JwtSettings
{
    public const string SectionName = "JwtSettings";

    /// <summary>
    /// 範本出貨的佔位金鑰共有的片段。**判準刻意是「樣式」而不是「字面值」**：
    /// <c>scripts/New-StarterProject.ps1</c> 會把前綴換成新專案代號
    /// （<c>DevelopmentOnly-</c> → <c>&lt;代號&gt;-</c>），精確比對會讓衍生專案的
    /// Production 防線與健康監控**靜默失效** —— 值還是佔位符，卻再也不相等。
    /// </summary>
    public const string PlaceholderSigningKeyMarker = "ChangeThisJwtSigningKey";

    /// <summary>金鑰是否仍是範本出貨的佔位值（含衍生專案換過前綴的形式）。</summary>
    public static bool IsPlaceholderSigningKey(string? signingKey) =>
        signingKey is not null
        && signingKey.Contains(PlaceholderSigningKeyMarker, StringComparison.Ordinal);

    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;

    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; set; } = 60;

    [Range(1, 365)]
    public int RefreshTokenDays { get; set; } = 7;

    [Range(0, 60)]
    public int ClockSkewMinutes { get; set; } = 2;
}
