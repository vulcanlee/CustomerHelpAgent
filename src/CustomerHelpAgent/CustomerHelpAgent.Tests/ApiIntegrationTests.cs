using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Dtos.Auths;
using CustomerHelpAgent.Dtos.Commons;
using CustomerHelpAgent.Dtos.Models;
using CustomerHelpAgent.Web;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Controllers;
using CustomerHelpAgent.Business.Services.DataAccess;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CustomerHelpAgent.Tests;

[Collection(nameof(IntegrationHostCollection))]
public sealed class ApiIntegrationTests : IClassFixture<ApiTestApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ApiTestApplicationFactory factory;

    public ApiIntegrationTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task ProtectedCrudApi_WithoutBearerToken_ShouldReturnApiResult401()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/Project/1");
        var result = await ReadApiResultAsync<object>(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(401, result.StatusCode);
        Assert.NotNull(result.TraceId);
    }

    [Fact]
    public async Task ProtectedCrudApi_WithoutRequiredPermission_ShouldReturnApiResult403()
    {
        var account = $"limited-{Guid.NewGuid():N}";
        const string password = "limited-pass";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var role = new RoleView
            {
                Name = $"受限角色-{Guid.NewGuid():N}",
                TabViewJson = JsonSerializer.Serialize(new[] { "首頁" }),
            };
            db.RoleView.Add(role);
            await db.SaveChangesAsync();

            db.MyUser.Add(new MyUser
            {
                Account = account,
                Name = "limited",
                Status = true,
                IsAdmin = false,
                RoleViewId = role.Id,
                Password = SecurePasswordHasher.HashPassword(password),
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto
        {
            Account = account,
            Password = password,
        });
        var loginResult = await ReadApiResultAsync<TokenResponseDto>(login);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", loginResult.Data!.AccessToken);

        var response = await client.GetAsync("/api/Project/1");
        var result = await ReadApiResultAsync<object>(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task ViewOnlyRole_CanRead_ButCannotCreate()
    {
        var account = $"viewer-{Guid.NewGuid():N}";
        const string password = "viewer-pass";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var writer = scope.ServiceProvider.GetRequiredService<CustomerHelpAgent.Business.Services.Other.IRbacWriteService>();

            var role = new RoleView
            {
                Name = $"唯讀-{Guid.NewGuid():N}",
                TabViewJson = JsonSerializer.Serialize(new[] { "專案項目:view" }),
            };
            db.RoleView.Add(role);
            await db.SaveChangesAsync();
            await writer.SyncRolePermissionsAsync(role.Id, new[] { "專案項目:view" });

            var user = new MyUser
            {
                Account = account,
                Name = "viewer",
                Status = true,
                IsAdmin = false,
                RoleViewId = role.Id,
                Password = SecurePasswordHasher.HashPassword(password),
            };
            db.MyUser.Add(user);
            await db.SaveChangesAsync();
            await writer.SyncUserRolesAsync(user.Id, new[] { role.Id });
        }

        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = account, Password = password });
        var loginResult = await ReadApiResultAsync<TokenResponseDto>(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Data!.AccessToken);

        // 有 view：讀取被允許（不存在的 id → 404，而非 403）
        var readResponse = await client.GetAsync("/api/Project/999999");
        Assert.Equal(HttpStatusCode.NotFound, readResponse.StatusCode);

        // 無 create：新增被拒（403）
        var createResponse = await client.PostAsJsonAsync("/api/Project", new ProjectCreateUpdateDto
        {
            Id = 0,
            Title = "should be forbidden",
            Status = "進行中",
            Priority = "中",
            Owner = "viewer",
        });
        Assert.Equal(HttpStatusCode.Forbidden, createResponse.StatusCode);
    }

    [Fact]
    public async Task AuthEndpoints_LoginRefreshAndMe_ShouldReturnApiResult()
    {
        using var client = factory.CreateClient();

        var loginResult = await LoginAsync(client);
        Assert.False(string.IsNullOrWhiteSpace(loginResult.Data?.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(loginResult.Data?.RefreshToken));

        var refreshResponse = await client.PostAsJsonAsync("/api/Auth/refresh", new RefreshTokenRequestDto
        {
            RefreshToken = loginResult.Data!.RefreshToken
        });
        var refreshResult = await ReadApiResultAsync<TokenResponseDto>(refreshResponse);

        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        Assert.True(refreshResult.Success);
        Assert.False(string.IsNullOrWhiteSpace(refreshResult.Data?.AccessToken));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Data.AccessToken);
        var meResponse = await client.GetAsync("/api/Auth/me");
        var meResult = await ReadApiResultAsync<CurrentUserDto>(meResponse);

        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        Assert.True(meResult.Success);
        Assert.Equal("support", meResult.Data?.Account);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccessToken_AfterSessionRevokedOrAccountDisabled_ShouldReturnApiResult401(bool disableAccount)
    {
        // 0.9.136 起 access token 也比對工作階段版本：改密碼、強制登出、停用帳號後，已發出的 token 立即失效，
        // 不再能用到過期（最多 60 分鐘）。
        var account = $"revoke-{Guid.NewGuid():N}";
        const string password = "revoke-pass";
        int userId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var user = new MyUser
            {
                Account = account,
                Name = "revoke",
                Status = true,
                IsAdmin = true,
                Password = SecurePasswordHasher.HashPassword(password),
            };
            db.MyUser.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = account, Password = password });
        var loginResult = await ReadApiResultAsync<TokenResponseDto>(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Data!.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Auth/me")).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var stamps = scope.ServiceProvider.GetRequiredService<CustomerHelpAgent.Business.Services.Other.ISecurityStampService>();
            if (disableAccount)
            {
                var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
                var user = await db.MyUser.SingleAsync(x => x.Id == userId);
                user.Status = false;
                await db.SaveChangesAsync();
                stamps.Invalidate(userId);
            }
            else
            {
                await stamps.RotateAsync(userId);
            }
        }

        var response = await client.GetAsync("/api/Auth/me");
        var result = await ReadApiResultAsync<CurrentUserDto>(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task VersionedAuthEndpoints_ShouldKeepApiResultContract()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto
        {
            Account = "support",
            Password = "support"
        });
        var result = await ReadApiResultAsync<TokenResponseDto>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Data?.AccessToken));
    }

    [Fact]
    public async Task ProjectCreate_InvalidPayload_ShouldReturnApiResult400()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var response = await client.PostAsJsonAsync("/api/Project", new { });
        var result = await ReadApiResultAsync<ProjectDto>(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(400, result.StatusCode);
        Assert.NotNull(result.Errors);
    }

    [Fact]
    public async Task ProjectCrud_WithBearerToken_ShouldUseApiResultAndDto()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var createDto = new ProjectCreateUpdateDto
        {
            Id = 1,
            Title = $"Integration Project {Guid.NewGuid():N}",
            Description = "Integration test project",
            StartDate = DateTime.Today,
            EndDate = DateTime.Today.AddDays(7),
            Status = "進行中",
            Priority = "中",
            CompletionPercentage = 10,
            Owner = "integration-test"
        };

        var createResponse = await client.PostAsJsonAsync("/api/Project", createDto);
        var createResult = await ReadApiResultAsync<ProjectDto>(createResponse);

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        Assert.True(createResult.Success);
        Assert.NotNull(createResult.Data);
        Assert.True(createResult.Data!.Id > 0);
        Assert.Equal(createDto.Title, createResult.Data.Title);

        var getResponse = await client.GetAsync($"/api/Project/{createResult.Data.Id}");
        var getResult = await ReadApiResultAsync<ProjectDto>(getResponse);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.True(getResult.Success);
        Assert.Equal(createDto.Title, getResult.Data?.Title);
    }

    // 樂觀並行（0.9.93 起）：PUT 必須帶 GET 取得的版本號；沒帶 400、別人先改過 409。
    // 建立時帶 Id = 0（新建、由資料庫配號）。0.9.94 起 POST 一律忽略客戶端的 Id，見 TeamCreate_ShouldIgnoreClientSuppliedId。
    [Fact]
    public async Task CategoryPut_ShouldRequireCurrentConcurrencyStamp()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);
        await AssertPutRequiresCurrentStampAsync<CategoryCreateUpdateDto, CategoryDto>(
            client,
            "/api/Category",
            new CategoryCreateUpdateDto { Id = 0, Name = $"並行分類 {Guid.NewGuid():N}", IsEnabled = true },
            x => x.Id,
            x => x.ConcurrencyStamp,
            (opened, stamp) => new CategoryCreateUpdateDto { Id = opened.Id, Name = $"並行分類 {Guid.NewGuid():N}", IsEnabled = true, ConcurrencyStamp = stamp });
    }

    [Fact]
    public async Task TeamPut_ShouldRequireCurrentConcurrencyStamp()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);
        await AssertPutRequiresCurrentStampAsync<TeamCreateUpdateDto, TeamDto>(
            client,
            "/api/Team",
            new TeamCreateUpdateDto { Id = 0, Name = $"並行團隊 {Guid.NewGuid():N}" },
            x => x.Id,
            x => x.ConcurrencyStamp,
            (opened, stamp) => new TeamCreateUpdateDto { Id = opened.Id, Name = $"並行團隊 {Guid.NewGuid():N}", ConcurrencyStamp = stamp });
    }

    [Fact]
    public async Task ProjectPut_ShouldRequireCurrentConcurrencyStamp()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);
        await AssertPutRequiresCurrentStampAsync<ProjectCreateUpdateDto, ProjectDto>(
            client,
            "/api/Project",
            NewProjectDto(0, null),
            x => x.Id,
            x => x.ConcurrencyStamp,
            (opened, stamp) => NewProjectDto(opened.Id, stamp));

        static ProjectCreateUpdateDto NewProjectDto(int? id, string? stamp) => new()
        {
            Id = id,
            Title = $"並行專案 {Guid.NewGuid():N}",
            StartDate = DateTime.Today,
            EndDate = DateTime.Today.AddDays(7),
            Status = "進行中",
            Priority = "中",
            CompletionPercentage = 10,
            Owner = "integration-test",
            ConcurrencyStamp = stamp,
        };
    }

    // 軟刪除（0.9.94 起）：API 的 DELETE 是軟刪除，之後 GET／PUT／DELETE 都回 404（FindAsync 套用軟刪除過濾）。
    [Fact]
    public async Task CategoryDelete_ShouldSoftDelete_AndHideTheRecordFromTheApi()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);
        var created = (await ReadApiResultAsync<CategoryDto>(await client.PostAsJsonAsync("/api/Category",
            new CategoryCreateUpdateDto { Id = 0, Name = $"軟刪除分類 {Guid.NewGuid():N}", IsEnabled = true }))).Data!;

        var delete = await client.DeleteAsync($"/api/Category/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/Category/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/Category/{created.Id}")).StatusCode);
        var put = await client.PutAsJsonAsync($"/api/Category/{created.Id}",
            new CategoryCreateUpdateDto { Id = created.Id, Name = created.Name, IsEnabled = true, ConcurrencyStamp = created.ConcurrencyStamp });
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    // 0.9.95 起使用者是軟刪除：已簽發的 JWT 在到期前仍然有效，權限判斷必須查不到已刪除的人（連管理員也一樣）。
    [Fact]
    public async Task SoftDeletedUsersToken_ShouldBeForbidden()
    {
        var account = $"deleted-{Guid.NewGuid():N}";
        const string password = "deleted-pass";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            db.MyUser.Add(new MyUser { Account = account, Name = "deleted", Status = true, IsAdmin = true, Password = SecurePasswordHasher.HashPassword(password) });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = account, Password = password });
        var loginResult = await ReadApiResultAsync<TokenResponseDto>(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Data!.AccessToken);
        var created = await client.PostAsJsonAsync("/api/Category", new CategoryCreateUpdateDto { Id = 0, Name = $"刪除前 {Guid.NewGuid():N}", IsEnabled = true });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            await db.MyUser.Where(x => x.Account == account).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsDeleted, true));
        }

        var afterDelete = await client.PostAsJsonAsync("/api/Category", new CategoryCreateUpdateDto { Id = 0, Name = $"刪除後 {Guid.NewGuid():N}", IsEnabled = true });
        Assert.Equal(HttpStatusCode.Forbidden, afterDelete.StatusCode);
    }

    // 0.9.93 之前 POST 會直接採用客戶端傳來的 Id，同一個 Id 第二次建立就撞主鍵回 500。
    [Fact]
    public async Task TeamCreate_ShouldIgnoreClientSuppliedId()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var first = await client.PostAsJsonAsync("/api/Team", new TeamCreateUpdateDto { Id = 999, Name = $"指定 Id 團隊 {Guid.NewGuid():N}" });
        var second = await client.PostAsJsonAsync("/api/Team", new TeamCreateUpdateDto { Id = 999, Name = $"指定 Id 團隊 {Guid.NewGuid():N}" });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotEqual((await ReadApiResultAsync<TeamDto>(first)).Data!.Id, (await ReadApiResultAsync<TeamDto>(second)).Data!.Id);
    }

    /// <summary>
    /// 同一套流程：建立 → GET 取得版本號 → PUT 沒帶版本號（400）→ 帶目前版本號（200）→ 再用同一個舊版本號（409）
    /// → GET 拿到的版本號已經換新。
    /// </summary>
    private static async Task AssertPutRequiresCurrentStampAsync<TWrite, TRead>(
        HttpClient client,
        string resource,
        TWrite createDto,
        Func<TRead, int?> getId,
        Func<TRead, string?> getStamp,
        Func<TRead, string?, TWrite> buildUpdate)
    {
        var created = await ReadApiResultAsync<TRead>(await client.PostAsJsonAsync(resource, createDto));
        Assert.True(created.Success, created.Message);
        var id = getId(created.Data!);

        var opened = (await ReadApiResultAsync<TRead>(await client.GetAsync($"{resource}/{id}"))).Data!;
        var stamp = getStamp(opened);
        Assert.False(string.IsNullOrWhiteSpace(stamp), "GET 的回應必須帶出 ConcurrencyStamp，API 用戶端才有辦法帶回來。");

        var missing = await client.PutAsJsonAsync($"{resource}/{id}", buildUpdate(opened, null));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var first = await client.PutAsJsonAsync($"{resource}/{id}", buildUpdate(opened, stamp));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var stale = await client.PutAsJsonAsync($"{resource}/{id}", buildUpdate(opened, stamp));
        var staleResult = await ReadApiResultAsync<object>(stale);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.False(staleResult.Success);

        var reopened = (await ReadApiResultAsync<TRead>(await client.GetAsync($"{resource}/{id}"))).Data!;
        Assert.NotEqual(stamp, getStamp(reopened));
    }

    [Fact]
    public async Task ForbiddenApi_ShouldReturnApiResult403()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var response = await client.GetAsync("/api/ContractProbe/forbidden");
        var result = await ReadApiResultAsync<object>(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(403, result.StatusCode);
        Assert.NotNull(result.TraceId);
    }

    [Fact]
    public async Task UnhandledApiException_ShouldReturnApiResult500()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var response = await client.GetAsync("/api/ContractProbe/throw");
        var result = await ReadApiResultAsync<object>(response);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(500, result.StatusCode);
        Assert.NotNull(result.Exception);
        Assert.Equal(typeof(InvalidOperationException).FullName, result.Exception.Type);
        Assert.NotNull(result.TraceId);
    }

    [Fact]
    public async Task UnhandledApiException_ShouldRecordAccountInExceptionLog()
    {
        // LOG-01：0.9.77 之前請求日誌中介軟體排在驗證之前，例外紀錄的帳號欄永遠是空的。
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        await client.GetAsync("/api/ContractProbe/throw");

        var row = await WaitForExceptionLogAsync(x => x.Message == "Integration probe exception.");
        Assert.Equal(ExceptionSources.WebApi, row.Source);
        Assert.Equal("support", row.Account);
        Assert.True(row.UserId > 0);
    }

    [Fact]
    public async Task UnhandledApiException_TraceIdShouldMatchExceptionLog()
    {
        // LOG-10：回應的 TraceId（錯誤追蹤碼）＝例外紀錄的 LastTraceId，使用者回報這個碼就能對上。
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var result = await ReadApiResultAsync<object>(await client.GetAsync("/api/ContractProbe/throw"));

        Assert.Matches("^[0-9A-Z]{8}$", result.TraceId);
        await WaitForExceptionLogAsync(x => x.Message == "Integration probe exception." && x.LastTraceId == result.TraceId);
    }

    [Fact]
    public async Task CaughtApiException_ShouldReturnTraceId()
    {
        // LOG-10：Controller 自己 catch 後回 500（ApiServerError）也要帶錯誤追蹤碼；0.9.78 之前是 null。
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var result = await ReadApiResultAsync<object>(await client.GetAsync("/api/ContractProbe/caught"));

        Assert.Matches("^[0-9A-Z]{8}$", result.TraceId);
    }

    [Fact]
    public async Task UnhandledApiException_ShouldRecordRouteTemplateAsPage()
    {
        // LOG-16：記路由樣板而不是帶 Id 的原始路徑，同一個錯誤不會因為 Id 不同散成好幾列。
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        await client.GetAsync("/api/ContractProbe/throw/1");
        await client.GetAsync("/api/ContractProbe/throw/2");

        await WaitForExceptionLogAsync(x => x.Message == "Integration probe route exception." && x.OccurrenceCount == 2);
        Assert.Equal(1, await CountExceptionLogsAsync(x => x.Message == "Integration probe route exception."));
        var row = await WaitForExceptionLogAsync(x => x.Message == "Integration probe route exception.");
        Assert.Equal("/api/ContractProbe/throw/{id}", row.Page);
    }

    [Fact]
    public async Task UnhandledPageException_ShouldBeRecordedOnlyOnce()
    {
        // LOG-03：非 API 請求的未處理例外會被 UseHttpRequestLogging 與框架的
        // ExceptionHandlerMiddleware（Testing 環境會啟用）各記一次，0.9.77 之前因此變成兩列。
        using var client = factory.CreateClient();
        var marker = Guid.NewGuid().ToString("N");

        var response = await client.GetAsync($"/contract-probe-page/throw?marker={marker}");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var message = $"Integration page probe exception {marker}.";
        var row = await WaitForExceptionLogAsync(x => x.Message == message);
        Assert.Equal(ExceptionSources.Ui, row.Source);
        Assert.Equal(1, row.OccurrenceCount);

        // 第二筆（若存在）由同一個寫入器緊接著寫入，留一點時間再數一次。
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await CountExceptionLogsAsync(x => x.Message == message));
    }

    [Fact]
    public async Task HealthReadiness_ShouldReturnHealthy()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthLiveness_ShouldReturnHealthy()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SystemHealthPage_WithoutCookieLogin_ShouldNotExposeDetails()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/system-health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK
            || response.StatusCode == HttpStatusCode.Redirect
            || response.StatusCode == HttpStatusCode.Unauthorized);
        Assert.DoesNotContain("最近 24 小時警告以上日誌", body);
    }

    [Fact]
    public void ProductionSafetyValidation_WithDevelopmentDefaults_ShouldFailFast()
    {
        var settings = new Dictionary<string, string?>
        {
            ["JwtSettings:SigningKey"] = "DevelopmentOnly-ChangeThisJwtSigningKey-AtLeast32Chars",
            ["BootstrapSettings:SupportAccount"] = "support",
            ["BootstrapSettings:SupportPassword"] = "support",
            ["Swagger:EnabledInProduction"] = ""
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StartupSafetyValidator.Validate(configuration, "Production"));

        Assert.Contains("JwtSettings:SigningKey", exception.Message);
        Assert.Contains("BootstrapSettings:SupportPassword", exception.Message);
        Assert.Contains("Swagger:EnabledInProduction", exception.Message);
    }

    /// <summary>
    /// 衍生專案形式的佔位金鑰同樣必須被擋下。
    /// ⚠️ 這條擋的是 0.9.47 查到的缺陷：原本用字面值精確比對，
    /// <c>New-StarterProject.ps1</c> 一把前綴換成專案代號，整道防線就靜默失效。
    /// </summary>
    [Fact]
    public void ProductionSafetyValidation_WithDerivedProjectPlaceholderKey_ShouldFailFast()
    {
        var configuration = BuildProductionSafeConfiguration(new Dictionary<string, string?>
        {
            ["JwtSettings:SigningKey"] = "Acme-ChangeThisJwtSigningKey-AtLeast32Chars",
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StartupSafetyValidator.Validate(configuration, "Production"));

        Assert.Contains("JwtSettings:SigningKey", exception.Message);
    }

    /// <summary>
    /// <c>SupportPassword</c> 留空不是「不設定」，而是把空字串雜湊成管理員密碼
    /// （<c>Program.cs</c> 的 seed），且每次重啟都重新套用一次 —— 必須擋下。
    /// </summary>
    [Fact]
    public void ProductionSafetyValidation_WithEmptySupportPassword_ShouldFailFast()
    {
        var configuration = BuildProductionSafeConfiguration(new Dictionary<string, string?>
        {
            ["BootstrapSettings:SupportPassword"] = string.Empty,
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StartupSafetyValidator.Validate(configuration, "Production"));

        Assert.Contains("BootstrapSettings:SupportPassword", exception.Message);
    }

    /// <summary>範本現行出貨的預設密碼 <c>support</c> 同樣必須被擋下。</summary>
    [Fact]
    public void ProductionSafetyValidation_WithTemplateSupportPassword_ShouldFailFast()
    {
        var configuration = BuildProductionSafeConfiguration(new Dictionary<string, string?>
        {
            ["BootstrapSettings:SupportPassword"] = "support",
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StartupSafetyValidator.Validate(configuration, "Production"));

        Assert.Contains("BootstrapSettings:SupportPassword", exception.Message);
    }

    /// <summary>
    /// 沒填 AI 金鑰等於功能關閉，Production 不該因此擋下啟動 ——
    /// 腳手架不帶金鑰也要能正常部署。
    /// </summary>
    [Fact]
    public void ProductionSafetyValidation_WithoutAiApiKey_ShouldNotComplainAboutAi()
    {
        var configuration = BuildProductionSafeConfiguration(new Dictionary<string, string?>
        {
            ["AiSettings:ApiKey"] = string.Empty,
            ["AiSettings:Endpoint"] = string.Empty,
            ["AiSettings:Model"] = string.Empty,
        });

        StartupSafetyValidator.Validate(configuration, "Production");
    }

    /// <summary>
    /// 反過來，有填金鑰代表操作者打算啟用，此時其餘設定必須完整。
    /// ⚠️ StartupSafetyValidator 讀的是字串鍵不經 POCO，改欄位名稱不會有編譯錯誤，
    /// 所以這一段一定要有測試蓋住。
    /// </summary>
    [Fact]
    public void ProductionSafetyValidation_WithAiApiKeyButIncompleteSettings_ShouldFailFast()
    {
        var configuration = BuildProductionSafeConfiguration(new Dictionary<string, string?>
        {
            ["AiSettings:Provider"] = "AzureOpenAI",
            ["AiSettings:ApiKey"] = "some-key",
            ["AiSettings:Endpoint"] = string.Empty,
            ["AiSettings:Model"] = string.Empty,
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StartupSafetyValidator.Validate(configuration, "Production"));

        Assert.Contains("AiSettings:Endpoint", exception.Message);
        Assert.Contains("AiSettings:Model", exception.Message);
    }

    /// <summary>OpenAI 不需要 Endpoint，只要有金鑰與模型就算完整。</summary>
    [Fact]
    public void ProductionSafetyValidation_WithCompleteOpenAiSettings_ShouldPass()
    {
        var configuration = BuildProductionSafeConfiguration(new Dictionary<string, string?>
        {
            ["AiSettings:Provider"] = "OpenAI",
            ["AiSettings:ApiKey"] = "some-key",
            ["AiSettings:Endpoint"] = string.Empty,
            ["AiSettings:Model"] = "gpt-4o-mini",
        });

        StartupSafetyValidator.Validate(configuration, "Production");
    }

    /// <summary>寄信是選配：沒設定（None）不該擋下 Production 啟動。</summary>
    [Fact]
    public void ProductionSafetyValidation_WithEmailProviderNone_ShouldPass()
    {
        var configuration = BuildProductionSafeConfiguration(new Dictionary<string, string?>
        {
            ["EmailSettings:Provider"] = "None",
        });

        StartupSafetyValidator.Validate(configuration, "Production");
    }

    /// <summary>Pickup 會把信（含日後的重設連結）以明文寫到磁碟，Production 一律擋下。</summary>
    [Theory]
    [InlineData("Pickup")]
    [InlineData("pickup")]
    public void ProductionSafetyValidation_WithPickupProvider_ShouldFailFast(string provider)
    {
        var configuration = BuildProductionSafeConfiguration(new Dictionary<string, string?>
        {
            ["EmailSettings:Provider"] = provider,
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StartupSafetyValidator.Validate(configuration, "Production"));

        Assert.Contains("EmailSettings:Provider", exception.Message);
    }

    [Fact]
    public void ProductionSafetyValidation_WithIncompleteSmtpSettings_ShouldFailFast()
    {
        var configuration = BuildProductionSafeConfiguration(new Dictionary<string, string?>
        {
            ["EmailSettings:Provider"] = "Smtp",
            ["EmailSettings:Host"] = string.Empty,
            ["EmailSettings:FromAddress"] = string.Empty,
            ["EmailSettings:PublicBaseUrl"] = string.Empty,
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StartupSafetyValidator.Validate(configuration, "Production"));

        Assert.Contains("EmailSettings:Host", exception.Message);
        Assert.Contains("EmailSettings:FromAddress", exception.Message);
        Assert.Contains("EmailSettings:PublicBaseUrl", exception.Message);
        // 現場排查用：要說出讀到的是空白，也要說明為什麼開發機（Development）不會被擋
        Assert.Contains("（空白）", exception.Message);
        Assert.Contains("ASPNETCORE_ENVIRONMENT", exception.Message);
    }

    /// <summary>公開網址必須是完整的 http(s) 網址，相對路徑或其他協定都不算。</summary>
    [Theory]
    [InlineData("erp.example.com")]
    [InlineData("/app")]
    [InlineData("ftp://erp.example.com")]
    public void ProductionSafetyValidation_WithSmtpAndInvalidPublicBaseUrl_ShouldFailFast(string publicBaseUrl)
    {
        var configuration = BuildProductionSafeConfiguration(new Dictionary<string, string?>
        {
            ["EmailSettings:Provider"] = "Smtp",
            ["EmailSettings:Host"] = "smtp.example.com",
            ["EmailSettings:FromAddress"] = "noreply@example.com",
            ["EmailSettings:PublicBaseUrl"] = publicBaseUrl,
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StartupSafetyValidator.Validate(configuration, "Production"));

        Assert.Contains("EmailSettings:PublicBaseUrl", exception.Message);
        Assert.Contains($"「{publicBaseUrl}」", exception.Message);
    }

    [Fact]
    public void ProductionSafetyValidation_WithCompleteSmtpSettings_ShouldPass()
    {
        var configuration = BuildProductionSafeConfiguration(new Dictionary<string, string?>
        {
            ["EmailSettings:Provider"] = "Smtp",
            ["EmailSettings:Host"] = "smtp.example.com",
            ["EmailSettings:FromAddress"] = "noreply@example.com",
            ["EmailSettings:PublicBaseUrl"] = "https://erp.example.com",
        });

        StartupSafetyValidator.Validate(configuration, "Production");
    }

    /// <summary>其餘設定都給安全值，讓測試只聚焦在傳入的那幾個鍵上。</summary>
    private static IConfiguration BuildProductionSafeConfiguration(Dictionary<string, string?> overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            ["JwtSettings:SigningKey"] = "ProductionSigningKey-AtLeast32CharactersLong-ok",
            ["BootstrapSettings:SupportAccount"] = "support",
            ["BootstrapSettings:SupportPassword"] = "a-real-password",
            ["Swagger:EnabledInProduction"] = "false",
        };

        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Fact]
    public async Task ProjectFileDownloadEndpoint_ShouldBeRegistered()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/api/project-files/1/download");

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// .NET 範本殘留的 WeatherForecastController 已於 0.4.34 移除。
    /// 它沒有任何授權標註、路由不在 /api 之下（連 ApiExceptionFilter 都不涵蓋），
    /// 卻會被 New-StarterProject.ps1 複製到每一個客戶專案。
    /// </summary>
    [Fact]
    public async Task WeatherForecastEndpoint_ShouldNotExist()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/WeatherForecast");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// 登入 Cookie 名稱必須帶專案名稱。Cookie 不分連接埠，兩個衍生專案若部署在同一主機名稱
    /// 的不同連接埠，又都沿用框架預設名 <c>.AspNetCore.CookieAuthenticationScheme</c>，
    /// 就會互相覆蓋、互相登出。期望值由組件名稱推算，所以 New-StarterProject.ps1 改名後依然有效。
    /// 專案名稱可以含點（例如 <c>Acme.Erp</c>），所以是去掉結尾的 <c>.Web</c>，不是取第一段。
    /// </summary>
    [Fact]
    public void AuthCookieName_ShouldBeProjectSpecific()
    {
        var assemblyName = typeof(Program).Assembly.GetName().Name!;
        var projectName = assemblyName[..^".Web".Length];
        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(MagicObjectHelper.CookieScheme);

        Assert.Equal($".{projectName}.Auth", options.Cookie.Name);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 0)]
    [InlineData(1, int.MaxValue)]
    public async Task Search_WithOutOfRangePaging_ShouldReturn400(int pageIndex, int pageSize)
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var response = await client.PostAsJsonAsync("/api/Project/search", new
        {
            PageIndex = pageIndex,
            PageSize = pageSize
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Controller 自己 catch 後回傳的 500，也必須遵守 Security:ReturnExceptionDetails。
    ///
    /// 0.4.34 之前只有 ApiExceptionFilter 會判斷這個開關；Controller catch 區塊呼叫的
    /// ApiServerError 直接把 exception.ToString() 與堆疊追蹤塞進回應，
    /// 導致 Production 設了 false 仍會外洩。
    /// </summary>
    [Fact]
    public async Task CaughtApiException_WithDetailsEnabled_ShouldIncludeExceptionInfo()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var response = await client.GetAsync("/api/ContractProbe/caught");
        var result = await ReadApiResultAsync<object>(response);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(result.Success);
        Assert.NotNull(result.Exception);
        Assert.NotNull(result.ErrorDetail);
    }

    /// <summary>
    /// nosniff 對本專案特別重要：附件下載會回吐儲存時記下的 ContentType，
    /// 少了它，瀏覽器可能把附件當成 HTML 解析而造成同源 XSS。
    /// </summary>
    [Fact]
    public async Task Responses_ShouldIncludeSecurityHeaders()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/health/live");

        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal(
            "strict-origin-when-cross-origin",
            Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }

    /// <summary>
    /// /UploadFiles 是把 DownloadPath 直接掛出去的靜態檔案端點。
    /// 0.4.35 之前它註冊在 UseAuthentication/UseAuthorization 之前，且 UseStaticFiles
    /// 本身不看授權，等於整個目錄匿名可讀。
    /// </summary>
    [Fact]
    public async Task UploadFilesStaticPath_WithoutLogin_ShouldNotBeAnonymouslyReadable()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/UploadFiles/anything.txt");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// 從**真實 DI 容器**解析 Blazor 路徑的資料服務。
    ///
    /// 這些服務只被 Razor 元件使用，整合測試的 HTTP 端點碰不到它們，
    /// 因此 0.4.36 把它們改注入 IDbContextFactory 之後，若 DI 沒接好
    /// （例如漏了 AddDbContextFactory），單靠既有測試不會發現 —— 要到執行期開頁面才炸。
    /// </summary>
    [Theory]
    [InlineData(typeof(CategoryService))]
    [InlineData(typeof(AfterSalesPolicyService))]
    [InlineData(typeof(ShopOrderService))]
    [InlineData(typeof(AfterSalesRequestService))]
    [InlineData(typeof(AgentTraceService))]
    [InlineData(typeof(AgentSessionStore))]
    [InlineData(typeof(ProcessMetricService))]
    [InlineData(typeof(TeamService))]
    [InlineData(typeof(RoleViewService))]
    [InlineData(typeof(ProjectService))]
    [InlineData(typeof(MyUserService))]
    public void DataAccessServices_ShouldResolveFromContainer(Type serviceType)
    {
        using var scope = factory.Services.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService(serviceType);

        Assert.NotNull(service);
    }

    /// <summary>售後 Agent 的 Runner 依賴十多個服務（1.0.4）；漏註冊任何一個都要到開「線上客服」頁才會炸。</summary>
    [Fact]
    public void AfterSalesAgentRunner_ShouldResolveFromContainer()
    {
        using var scope = factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CustomerHelpAgent.Web.Agents.AfterSales.AfterSalesAgentRunner>());
    }

    [Fact]
    public void DbContextFactory_ShouldResolveAndCreateContext()
    {
        using var scope = factory.Services.CreateScope();

        var contextFactory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<BackendDBContext>>();
        using var context = contextFactory.CreateDbContext();

        Assert.NotNull(context);
    }

    /// <summary>
    /// 例外紀錄由背景寫入器非同步寫入，輪詢等它出現（最多約 5 秒）。
    /// </summary>
    private async Task<ExceptionLog> WaitForExceptionLogAsync(System.Linq.Expressions.Expression<Func<ExceptionLog, bool>> predicate)
    {
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var row = await context.ExceptionLog.AsNoTracking().FirstOrDefaultAsync(predicate);
            if (row is not null)
            {
                return row;
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException("等待 5 秒仍未在系統例外紀錄中看到預期的例外。");
    }

    private async Task<int> CountExceptionLogsAsync(System.Linq.Expressions.Expression<Func<ExceptionLog, bool>> predicate)
    {
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.ExceptionLog.CountAsync(predicate);
    }

    private static async Task AuthorizeAsync(HttpClient client)
    {
        var loginResult = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Data!.AccessToken);
    }

    private static async Task<ApiResult<TokenResponseDto>> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto
        {
            Account = "support",
            Password = "support"
        });

        var result = await ReadApiResultAsync<TokenResponseDto>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        return result;
    }

    private static async Task<ApiResult<T>> ReadApiResultAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResult<T>>(json, JsonOptions);
        Assert.NotNull(result);
        return result!;
    }
}

public class ApiTestApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "CustomerHelpAgentIntegrationTests",
        Guid.NewGuid().ToString("N"));

    private readonly Dictionary<string, string> environmentVariables;

    public ApiTestApplicationFactory()
    {
        // Program.cs 結束時呼叫 LogManager.Shutdown()，全行程的 NLog 設定會被清成 null，
        // 而且不會再自動讀 nlog.config。同一個測試行程裡「前一個 host 已停止、下一個 host 才啟動」時，
        // NLog.Web 找不到設定就改讀 appsettings 的 NLog 區段，碰到 BasePath 直接丟 NLogConfigurationException。
        // 在 host 啟動之前把 nlog.config 載回來，讓每個 host 都拿到與正式環境相同的設定。
        if (NLog.LogManager.Configuration is null)
        {
            NLog.LogManager.Configuration = new NLog.Config.XmlLoggingConfiguration(
                Path.Combine(AppContext.BaseDirectory, "nlog.config"));
        }

        environmentVariables = CreateSettings()
            .ToDictionary(
                x => x.Key.Replace(":", "__", StringComparison.Ordinal),
                x => x.Value ?? string.Empty);

        foreach (var item in environmentVariables)
        {
            Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(CreateSettings());
        });
        builder.ConfigureServices(services =>
        {
            services.AddAuthorization(options =>
            {
                options.AddPolicy("IntegrationForbidden", policy =>
                    policy.RequireClaim("integration_forbidden", "true"));
            });

            services
                .AddControllers()
                .PartManager
                .ApplicationParts
                .Add(new AssemblyPart(typeof(ContractProbeController).Assembly));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        foreach (var item in environmentVariables)
        {
            Environment.SetEnvironmentVariable(item.Key, null);
        }

        if (disposing && Directory.Exists(rootPath))
        {
            // 0.9.91 起資料庫是 WAL 模式（多出 -wal／-shm 檔）；連線池佔著這些檔案時目錄刪不掉。
            // 連線字串必須與 AddConfiguredDatabase 組出來的完全相同，連線池以字串為鍵。
            SqliteConnection.ClearPool(new SqliteConnection(new SqliteConnectionStringBuilder(
                MagicObjectHelper.GetSQLiteConnectionString(Path.Combine(rootPath, "DB")))
            {
                ForeignKeys = true,
            }.ToString()));

            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch (IOException)
            {
                // SQLite may release file handles shortly after the test host stops.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup keeps integration test assertions independent from OS file timing.
            }
        }
    }

    private Dictionary<string, string?> CreateSettings()
    {
        return new Dictionary<string, string?>
        {
            ["NLog:BasePath"] = Path.Combine(rootPath, "Logs"),
            ["Security:ReturnExceptionDetails"] = "true",

            // 測試會反覆登入，不應受正式配額牽制（正式預設為 120 / 10）。
            // 限流本身的行為另由單元測試驗證分割鍵，不靠整合測試踩配額。
            ["RateLimit:ApiRequestsPerMinute"] = "100000",
            ["RateLimit:LoginRequestsPerMinute"] = "100000",
            ["JwtSettings:Issuer"] = "CustomerHelpAgent.Tests",
            ["JwtSettings:Audience"] = "CustomerHelpAgent.Tests.Api",
            ["JwtSettings:SigningKey"] = "IntegrationTests-ChangeThisJwtSigningKey-AtLeast32Chars",
            ["JwtSettings:AccessTokenMinutes"] = "30",
            ["JwtSettings:RefreshTokenDays"] = "7",
            ["JwtSettings:ClockSkewMinutes"] = "0",
            ["BootstrapSettings:SupportAccount"] = "support",
            ["BootstrapSettings:SupportName"] = "support",
            ["BootstrapSettings:SupportEmail"] = "support",
            ["BootstrapSettings:SupportPassword"] = "support",
            ["SystemSettings:ExternalFileSystem:DatabasePath"] = Path.Combine(rootPath, "DB"),
            ["SystemSettings:ExternalFileSystem:DownloadPath"] = Path.Combine(rootPath, "Download"),
            ["SystemSettings:ExternalFileSystem:UploadPath"] = Path.Combine(rootPath, "Upload"),
            ["SystemSettings:ExternalFileSystem:ProjectFilePath"] = Path.Combine(rootPath, "ProjectFile"),
            ["SystemSettings:ExternalFileSystem:AfterSalesFilePath"] = Path.Combine(rootPath, "AfterSalesFile"),
            // 一定要跟著改到 rootPath：整合測試會啟動真實 host，例外記錄管線是活的。
            // 漏掉這一行，測試就會把堆疊檔寫進開發者（或 CI）真正的 ExceptionPath，
            // 而資料列卻留在測試自己的資料庫裡 —— 留下一堆對不到紀錄的孤兒檔。
            ["SystemSettings:ExternalFileSystem:ExceptionPath"] = Path.Combine(rootPath, "Exception"),
            ["SystemSettings:ExternalFileSystem:TokenUsagePath"] = Path.Combine(rootPath, "TokenUsage"),
            // ⚠️ 同理，而且更危險：AI 對話紀錄的自動過期是背景服務，host 一啟動就會真的去清月份目錄。
            // 漏掉這一行，測試會刪到開發者（或 CI）真正的 AiCallLogPath 內容（0.9.72 起）。
            ["SystemSettings:ExternalFileSystem:AiCallLogPath"] = Path.Combine(rootPath, "AiCallLog"),
            // 同理：稽核的詳細內容檔（修改差異、查詢條件）會真的寫到磁碟（0.9.131 起）。
            ["SystemSettings:ExternalFileSystem:AuditContentPath"] = Path.Combine(rootPath, "AuditContent"),
            // 同理：整合測試會啟動真實 host，Data Protection 會真的把金鑰環寫到磁碟。
            // 漏掉這一行，測試就會把金鑰寫進開發者（或 CI）真正的金鑰目錄。
            ["SystemSettings:ExternalFileSystem:DataProtectionKeyPath"] = Path.Combine(rootPath, "Keys"),
            // 同理：備份作業會把資料庫與檔案打包寫到這裡（0.9.99 起）。
            ["SystemSettings:ExternalFileSystem:BackupPath"] = Path.Combine(rootPath, "Backup"),
            // 明寫 None：開發者本機若用 User Secrets／環境變數開了 Smtp，整合測試也不可以真的寄信。
            // Pickup 資料夾同樣指到 rootPath，切換 provider 的子類才不會寫進真正的信件資料夾。
            ["EmailSettings:Provider"] = "None",
            ["EmailSettings:PickupDirectory"] = Path.Combine(rootPath, "Mails")
        };
    }
}

/// <summary>
/// 與 <see cref="ApiTestApplicationFactory"/> 相同，但把
/// <c>Security:ReturnExceptionDetails</c> 關掉，用來驗證 Production 組態下不會外洩堆疊。
/// </summary>
public sealed class ApiTestApplicationFactoryWithoutExceptionDetails : ApiTestApplicationFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // 註冊順序在 base 之後，因此覆寫得掉 base 加進去的設定。
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ReturnExceptionDetails"] = "false"
            });
        });
    }
}

/// <summary>
/// 迴歸測試：Production 組態（ReturnExceptionDetails=false）下，
/// **兩條 500 路徑**都不得回傳例外訊息或堆疊追蹤。
///
/// 路徑一：未攔截的例外 → ApiExceptionFilter（原本就有判斷）
/// 路徑二：Controller 自行 catch → ApiServerError（0.4.34 之前完全繞過判斷）
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class ApiExceptionDetailSuppressionTests
    : IClassFixture<ApiTestApplicationFactoryWithoutExceptionDetails>
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly ApiTestApplicationFactoryWithoutExceptionDetails factory;

    public ApiExceptionDetailSuppressionTests(ApiTestApplicationFactoryWithoutExceptionDetails factory)
    {
        this.factory = factory;
    }

    [Theory]
    [InlineData("/api/ContractProbe/throw")]
    [InlineData("/api/ContractProbe/caught")]
    public async Task ServerError_WithDetailsDisabled_ShouldNotLeakExceptionInfo(string url)
    {
        using var client = factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto
        {
            Account = "support",
            Password = "support"
        });
        var login = JsonSerializer.Deserialize<ApiResult<TokenResponseDto>>(
            await loginResponse.Content.ReadAsStringAsync(), JsonOptions)!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.Data!.AccessToken);

        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResult<object>>(body, JsonOptions)!;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(500, result.StatusCode);

        // 回應仍是 ApiResult 信封，但不得夾帶任何例外細節。
        Assert.Null(result.Exception);
        Assert.Null(result.ErrorDetail);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Integration probe", body, StringComparison.Ordinal);
    }
}

/// <summary>登入配額壓到 2，用來驗證限流真的會觸發且回應維持 ApiResult 信封。</summary>
public sealed class ApiTestApplicationFactoryWithTightLoginLimit : ApiTestApplicationFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimit:LoginRequestsPerMinute"] = "2"
            });
        });
    }
}

/// <summary>
/// 限流的兩個迴歸點，**都是實跑才發現的**：
///
/// 1. 登入的較嚴格配額原本用 `[EnableRateLimiting("login")]` 屬性指定，
///    但 `MapControllers().RequireRateLimiting("api")` 這個端點慣例套用時機晚於屬性，
///    會把它蓋掉 —— 登入配額**靜默失效**。改為在同一個 policy 內依路徑判斷。
/// 2. 限流拒絕原本不寫 body，空 body 的 429 會被 `UseStatusCodePagesWithReExecute`
///    拿原始的 POST + JSON 去重跑 `/not-found`（Blazor 頁面），被 antiforgery 擋下後
///    變成 **400 HTML** —— 呼叫端根本看不出真正發生什麼事。
/// </summary>
/// <remarks>
/// ⚠️ 這裡刻意是**一個測試**，不是兩個 —— 拆開會變成擲骰子。
///
/// 登入配額壓到 2，而限流分區鍵是 <c>login:</c> ＋ 使用者／IP／<c>anonymous</c>；
/// <c>WebApplicationFactory</c> 裡 <c>RemoteIpAddress</c> 是 null，所以同一個 factory 底下的
/// 每個測試都落在**同一個分區**。拆成兩個測試時，先跑的那個把配額吃光，後跑的必定失敗。
///
/// 而 xUnit 依測試案例 UniqueID 的雜湊排序，UniqueID **含組件名稱** ——
/// 腳手架衍生出的專案叫什麼名字，就決定了誰先跑，也就決定新專案第一天的測試是綠是紅。
/// 0.9.48 以控制組實驗確認：同樣命名為 PruneApp、但完全不清文件的副本一樣會紅。
///
/// 也**不要**改成「每個測試各自 <c>new</c> 一個 factory」：
/// <see cref="ApiTestApplicationFactory"/> 的 <c>Dispose</c> 會把它設定的環境變數清掉，
/// 同一個處理程序裡先棄置再建立第二個，NLog 會在重新載入設定時丟
/// <c>Unrecognized value 'BasePath'</c> 而讓測試爆掉。
/// </remarks>
[Collection(nameof(IntegrationHostCollection))]
public sealed class LoginRateLimitTests : IClassFixture<ApiTestApplicationFactoryWithTightLoginLimit>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ApiTestApplicationFactoryWithTightLoginLimit factory;

    public LoginRateLimitTests(ApiTestApplicationFactoryWithTightLoginLimit factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task LoginQuota_ShouldReturn429WithEnvelope_AndNotConsumeGeneralApiQuota()
    {
        using var client = factory.CreateClient();
        var request = new LoginRequestDto { Account = "nobody", Password = "wrong" };

        // 配額 2：前兩次應為 401（帳密錯誤），之後才被限流。
        for (var i = 0; i < 2; i++)
        {
            var allowed = await client.PostAsJsonAsync("/api/Auth/login", request);
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        var response = await client.PostAsJsonAsync("/api/Auth/login", request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        // 必須是 ApiResult JSON，不能是被錯誤頁重跑後的 HTML。
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var result = JsonSerializer.Deserialize<ApiResult<object>>(body, JsonOptions)!;
        Assert.False(result.Success);
        Assert.Equal(429, result.StatusCode);

        // 登入配額已經吃光，但一般 API 是另一個分區（前綴 api:）：
        // 未帶 token 應為 401；若是 429 就代表兩者共用了計數器。
        var generalApi = await client.PostAsJsonAsync("/api/Project/search", new { PageIndex = 1, PageSize = 10 });
        Assert.Equal(HttpStatusCode.Unauthorized, generalApi.StatusCode);
    }
}

[ApiController]
[Route("api/[controller]")]
public sealed class ContractProbeController : ControllerBase
{
    [HttpGet("forbidden")]
    [Authorize(
        AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
        Policy = "IntegrationForbidden")]
    public IActionResult ForbiddenProbe()
    {
        return Ok();
    }

    [HttpGet("throw")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public IActionResult ThrowProbe()
    {
        throw new InvalidOperationException("Integration probe exception.");
    }

    /// <summary>帶 Id 的路由：LOG-16 驗證例外紀錄的頁面記的是路由樣板。</summary>
    [HttpGet("throw/{id}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public IActionResult ThrowWithIdProbe(int id)
    {
        throw new InvalidOperationException("Integration probe route exception.");
    }

    /// <summary>
    /// 不在 /api 之下的未處理例外：ApiExceptionFilter 不接手，會一路冒泡到
    /// UseHttpRequestLogging 與框架的 ExceptionHandlerMiddleware（LOG-03 的重複記錄路徑）。
    /// </summary>
    [HttpGet("/contract-probe-page/throw")]
    [AllowAnonymous]
    public IActionResult PageThrowProbe([FromQuery] string marker)
    {
        throw new InvalidOperationException($"Integration page probe exception {marker}.");
    }

    /// <summary>
    /// 模擬 Controller 自行 catch 後回 500 的路徑（正式 Controller 共 16 處這樣寫）。
    /// 與 <see cref="ThrowProbe"/> 的差別在於它不會冒泡到 ApiExceptionFilter。
    /// </summary>
    [HttpGet("caught")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public IActionResult CaughtProbe()
    {
        try
        {
            throw new InvalidOperationException("Integration probe caught exception.");
        }
        catch (Exception ex)
        {
            return this.ApiServerError("探針錯誤", ex);
        }
    }
}
