using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Dtos.Auths;
using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Web.Auth;

public class JwtTokenService : IJwtTokenService
{
    internal const string TokenTypeClaimType = "token_type";
    private const string RefreshTokenType = "refresh";
    internal const string AccessTokenType = "access";
    private readonly JwtSettings settings;
    private readonly ILogger<JwtTokenService> logger;

    public JwtTokenService(IOptions<JwtSettings> options, ILogger<JwtTokenService> logger)
    {
        settings = options.Value;
        this.logger = logger;
    }

    public TokenResponseDto CreateTokenResponse(MyUser user)
    {
        var currentUser = ToCurrentUserDto(user);
        var accessExpiresAt = DateTime.UtcNow.AddMinutes(settings.AccessTokenMinutes);
        var refreshExpiresAt = DateTime.UtcNow.AddDays(settings.RefreshTokenDays);

        // ⚠️ 只記 UserId 與到期時間，token 本身絕不進日誌。
        logger.LogDebug(
            "Issued access and refresh tokens. UserId={UserId}, AccessExpiresAt={AccessExpiresAt}, RefreshExpiresAt={RefreshExpiresAt}",
            currentUser.Id, accessExpiresAt, refreshExpiresAt);

        return new TokenResponseDto
        {
            AccessToken = CreateToken(currentUser, user.SecurityStamp, AccessTokenType, accessExpiresAt),
            AccessTokenExpiresAt = accessExpiresAt,
            RefreshToken = CreateToken(currentUser, user.SecurityStamp, RefreshTokenType, refreshExpiresAt),
            RefreshTokenExpiresAt = refreshExpiresAt,
            User = currentUser
        };
    }

    public RefreshTokenIdentity ValidateRefreshToken(string refreshToken)
    {
        var principal = new JwtSecurityTokenHandler().ValidateToken(
            refreshToken,
            CreateValidationParameters(validateLifetime: true),
            out _);

        var tokenType = principal.FindFirstValue(TokenTypeClaimType);
        if (!string.Equals(tokenType, RefreshTokenType, StringComparison.Ordinal))
        {
            // 拿 access token 來換 refresh 屬「可能的誤用」（§3.1）。呼叫端（AuthController）會再記一筆並回 401。
            logger.LogWarning("Refresh token rejected because the token type is not refresh. Kind={Kind}", tokenType);
            throw new SecurityTokenException("Token 類型不是 refresh token。");
        }

        var user = new CurrentUserDto
        {
            Id = int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0"),
            Account = principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
            Name = principal.FindFirstValue("display_name") ?? string.Empty,
            Email = principal.FindFirstValue(ClaimTypes.Email),
            IsAdmin = bool.TryParse(principal.FindFirstValue("is_admin"), out var isAdmin) && isAdmin
        };

        // 工作階段版本另外回傳，不放進 /me 回傳給前端的 DTO（0.9.103 起；之前簽發的 token 沒有這個 claim → 空字串 → 比對不符）。
        return new RefreshTokenIdentity(user, principal.FindFirstValue(MagicObjectHelper.SecurityStampClaimType) ?? string.Empty);
    }

    public TokenValidationParameters CreateValidationParameters(bool validateLifetime)
    {
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            ValidateAudience = true,
            ValidAudience = settings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = CreateSecurityKey(),
            ValidateLifetime = validateLifetime,
            ClockSkew = TimeSpan.FromMinutes(settings.ClockSkewMinutes)
        };
    }

    private string CreateToken(CurrentUserDto user, string securityStamp, string tokenType, DateTime expiresAt)
    {
        var credentials = new SigningCredentials(CreateSecurityKey(), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Account),
            new("display_name", user.Name),
            new("is_admin", user.IsAdmin.ToString()),
            new(TokenTypeClaimType, tokenType),
            new(MagicObjectHelper.SecurityStampClaimType, securityStamp)
        };

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        var token = new JwtSecurityToken(
            issuer: settings.Issuer,
            audience: settings.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private SymmetricSecurityKey CreateSecurityKey()
    {
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey));
    }

    private static CurrentUserDto ToCurrentUserDto(MyUser user)
    {
        return new CurrentUserDto
        {
            Id = user.Id,
            Account = user.Account,
            Name = user.Name,
            Email = user.Email,
            IsAdmin = user.IsAdmin
        };
    }
}
