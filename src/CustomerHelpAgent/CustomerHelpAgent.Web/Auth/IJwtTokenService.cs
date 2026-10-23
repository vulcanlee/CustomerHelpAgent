using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Dtos.Auths;

namespace CustomerHelpAgent.Web.Auth;

public interface IJwtTokenService
{
    TokenResponseDto CreateTokenResponse(MyUser user);

    RefreshTokenIdentity ValidateRefreshToken(string refreshToken);
}

/// <summary>refresh token 驗證後的身分與它簽發時的工作階段版本（0.9.103 起）。</summary>
public sealed record RefreshTokenIdentity(CurrentUserDto User, string SecurityStamp);
