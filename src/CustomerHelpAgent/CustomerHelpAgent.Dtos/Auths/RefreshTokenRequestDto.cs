using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Dtos.Auths;

public class RefreshTokenRequestDto
{
    [Required(ErrorMessage = "Refresh Token 不可為空白")]
    public string RefreshToken { get; set; } = string.Empty;
}
