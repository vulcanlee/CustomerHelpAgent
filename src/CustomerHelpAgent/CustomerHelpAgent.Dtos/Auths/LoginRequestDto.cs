using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Dtos.Auths;

public class LoginRequestDto
{
    [Required(ErrorMessage = "帳號不可為空白")]
    public string Account { get; set; } = string.Empty;

    [Required(ErrorMessage = "密碼不可為空白")]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// 兩步驟驗證碼或備用碼（0.9.104 起）。帳號已啟用兩步驟驗證時必填；沒帶會回 401「需要兩步驟驗證碼」（不計入失敗次數），帶錯會計入。
    /// </summary>
    public string? TwoFactorCode { get; set; }
}
