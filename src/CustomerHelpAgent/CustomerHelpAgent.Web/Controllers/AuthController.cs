using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Dtos.Auths;
using CustomerHelpAgent.Dtos.Commons;
using CustomerHelpAgent.Web.Auth;
using CustomerHelpAgent.Web.Filters;

namespace CustomerHelpAgent.Web.Controllers;

[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
public class AuthController : ControllerBase
{
    private readonly MyUserServiceLogin userServiceLogin;
    private readonly IJwtTokenService jwtTokenService;
    private readonly ITwoFactorService twoFactorService;
    private readonly ILogger<AuthController> logger;

    public AuthController(
        MyUserServiceLogin userServiceLogin,
        IJwtTokenService jwtTokenService,
        ITwoFactorService twoFactorService,
        ILogger<AuthController> logger)
    {
        this.userServiceLogin = userServiceLogin;
        this.jwtTokenService = jwtTokenService;
        this.twoFactorService = twoFactorService;
        this.logger = logger;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    // 註：登入的較嚴格配額由 "api" policy 依路徑判斷（見 AddConfiguredRateLimiting）。
    // 這裡刻意**不用** [EnableRateLimiting("login")]：端點慣例
    // MapControllers().RequireRateLimiting("api") 套用時機晚於屬性，會把它蓋掉而靜默失效。
    public async Task<ActionResult<ApiResult<TokenResponseDto>>> Login([FromBody] LoginRequestDto request)
    {
        var attempt = await userServiceLogin.LoginAsync(request.Account, request.Password);
        var (message, user) = attempt;

        // 已啟用兩步驟驗證（0.9.104 起）：API 也要第二步，不能留後門。沒帶驗證碼只提示、不計入失敗次數。
        if (attempt.RequiresTwoFactor && attempt.User is { } pendingUser)
        {
            if (string.IsNullOrWhiteSpace(request.TwoFactorCode))
            {
                logger.LogInformation("API login requires the second factor. UserId={UserId}", pendingUser.Id);
                return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult("需要兩步驟驗證碼。"));
            }

            (message, user) = await userServiceLogin.CompleteSecondFactorAsync(pendingUser.Id, request.TwoFactorCode);
        }

        if (user is null)
        {
            logger.LogWarning("API login failed. Account={Account}", request.Account);
            return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult(message));
        }

        // 必須使用兩步驟驗證卻還沒設定：網頁會帶去設定頁，API 沒有地方設定，直接拒絕（否則等於可以用 API 繞過強制）。
        if (!user.TwoFactorEnabled && await twoFactorService.IsRequiredAsync(user.Id))
        {
            logger.LogInformation("API login refused because two-factor setup is required. UserId={UserId}", user.Id);
            return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult("請先在網頁完成兩步驟驗證設定。"));
        }

        var tokenResponse = jwtTokenService.CreateTokenResponse(user);
        logger.LogInformation("API login succeeded. Account={Account}, UserId={UserId}", user.Account, user.Id);
        return Ok(ApiResult<TokenResponseDto>.SuccessResult(tokenResponse, "登入成功"));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResult<TokenResponseDto>>> Refresh([FromBody] RefreshTokenRequestDto request)
    {
        try
        {
            var (currentUser, tokenStamp) = jwtTokenService.ValidateRefreshToken(request.RefreshToken);

            // ⚠️ 不可直接拿 token claim 裡的資料重簽。
            // Refresh token 是 stateless、不落庫、無法撤銷（見「認證授權與權限機制」的既有限制），
            // 若不回查資料庫，帳號被停用或降權之後，舊 refresh token 在有效期內
            // 仍可持續換發帶著舊 IsAdmin 的 access token。
            var user = await userServiceLogin.GetActiveUserAsync(currentUser.Id);
            if (user is null)
            {
                logger.LogWarning(
                    "Refresh rejected because the user no longer exists or is disabled. UserId={UserId}",
                    currentUser.Id);
                await this.WriteAuditAsync(
                    AuditActions.Token.RefreshFailed, "MyUser", currentUser.Id.ToString(), "reason=UserInactive", success: false);
                return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult("Refresh Token 無效或已過期。"));
            }

            // 工作階段已失效（改密碼、停用、角色變更、強制登出之後，0.9.103 起）：舊的 refresh token 不能再換發。
            if (!SecurityStamps.Matches(tokenStamp, user.SecurityStamp))
            {
                logger.LogInformation("Refresh rejected because the session was revoked. UserId={UserId}", currentUser.Id);
                await this.WriteAuditAsync(
                    AuditActions.Token.RefreshFailed, "MyUser", currentUser.Id.ToString(), "reason=SessionRevoked", success: false);
                return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult("Refresh Token 無效或已過期。"));
            }

            var tokenResponse = jwtTokenService.CreateTokenResponse(user);
            return Ok(ApiResult<TokenResponseDto>.SuccessResult(tokenResponse, "Token 更新成功"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Refresh token validation failed.");
            // 只記失敗原因的類別，token 本身與例外訊息都不進稽核。
            await this.WriteAuditAsync(
                AuditActions.Token.RefreshFailed, "Token", null, $"reason=InvalidToken; error={ex.GetType().Name}", success: false);
            return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult("Refresh Token 無效或已過期。"));
        }
    }

    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public ActionResult<ApiResult<CurrentUserDto>> Me()
    {
        var user = new CurrentUserDto
        {
            Id = int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0,
            Account = User.Identity?.Name ?? string.Empty,
            Name = User.FindFirst("display_name")?.Value ?? string.Empty,
            Email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
            IsAdmin = bool.TryParse(User.FindFirst("is_admin")?.Value, out var isAdmin) && isAdmin
        };

        return Ok(ApiResult<CurrentUserDto>.SuccessResult(user, "取得目前使用者成功"));
    }
}
