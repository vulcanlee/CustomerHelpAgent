using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Web.Auth;
using CustomerHelpAgent.Web.Diagnostics;

namespace CustomerHelpAgent.Web.Extensions;

public static class ApplicationBuilderExtensions
{
    public static WebApplication UseConfiguredSwagger(this WebApplication app, ILogger logger)
    {
        var swaggerSettings = app.Configuration
            .GetSection(SwaggerSettings.SectionName)
            .Get<SwaggerSettings>() ?? new SwaggerSettings();

        if (!app.Environment.IsDevelopment()
            && !(app.Environment.IsProduction() && swaggerSettings.EnabledInProduction))
        {
            return app;
        }

        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "CustomerHelpAgent API v1");
        });
        logger.LogInformation("Swagger UI enabled.");
        return app;
    }

    public static WebApplication UseConfiguredForwardedHeaders(this WebApplication app)
    {
        var settings = app.Configuration
            .GetSection(ForwardedHeadersSettings.SectionName)
            .Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };

        // 只在確實設定了信任來源時才放寬；沒設定就維持 ASP.NET Core 預設（僅信任 loopback），
        // 避免任何呼叫端都能偽造 X-Forwarded-For 來繞過以 IP 分割的限流。
        foreach (var proxy in settings.KnownProxies)
        {
            if (IPAddress.TryParse(proxy, out var address))
            {
                options.KnownProxies.Add(address);
            }
        }

        foreach (var network in settings.KnownNetworks)
        {
            var parts = network.Split('/', 2);
            if (parts.Length == 2
                && IPAddress.TryParse(parts[0], out var prefix)
                && int.TryParse(parts[1], out var prefixLength))
            {
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, prefixLength));
            }
        }

        app.UseForwardedHeaders(options);

        return app;
    }

    /// <summary>
    /// 基本安全回應標頭。
    ///
    /// <c>nosniff</c> 尤其重要：專案的附件下載會回吐儲存時記下的 ContentType，
    /// 少了它，瀏覽器可能把附件當成 HTML 解析而造成同源 XSS。
    ///
    /// CSP 刻意不放在這裡：Blazor Server 有自己的 CSP 需求，且
    /// <c>App.razor</c> 會從 fonts.googleapis.com 載入字型、AntDesign 會注入 inline style。
    /// 導入時請先以 <c>Content-Security-Policy-Report-Only</c> 觀察，確認無誤再轉為正式標頭。
    /// </summary>
    public static WebApplication UseSecurityHeaders(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

            await next();
        });

        return app;
    }

    /// <summary>
    /// Host 標頭空白或組不成網址的請求直接回 400。
    ///
    /// HTTP/1.0 可以不帶 Host（掃描器、直接打 IP 的機器人常見）；這種請求進到 Blazor 頁面時，
    /// 組 BaseUri 會得到 <c>https:///</c> 而拋 UriFormatException，連重跑的 /Error 都跟著失敗。
    /// <c>AllowedHosts</c> 擋不到：HostFiltering 預設 <c>AllowEmptyHosts = true</c>。
    /// 用 <c>Uri.TryCreate</c> 判斷，與 Blazor 實際失敗的那一步一致。
    /// </summary>
    public static WebApplication UseRejectInvalidHost(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (!Uri.TryCreate($"{context.Request.Scheme}://{context.Request.Host}/", UriKind.Absolute, out _))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            await next();
        });

        return app;
    }

    public static WebApplication UseConfiguredCors(this WebApplication app)
    {
        app.UseCors("ConfiguredCors");
        return app;
    }

    /// <summary>
    /// 不值得記在 Information 的路徑前綴：靜態資產、框架端點與健康探針。
    ///
    /// 這些請求量遠大於使用者真正的操作 —— 光是每 10 秒一次的 /health/live 就是一天
    /// 8,640 筆，會把「使用者做了什麼」整個淹掉。它們改記在 Debug，需要時仍查得到。
    /// </summary>
    private static readonly string[] LowValueRequestPrefixes =
    [
        "/_framework", "/_content", "/_blazor", "/css", "/js", "/lib",
        "/health", "/favicon", "/UploadFiles", "/swagger",
    ];

    /// <summary>
    /// 慢 HTTP 請求（LOG-21）。
    /// ⚠️ 排除 <c>/_blazor</c>：Blazor 的 WebSocket 連線本身就是一個「請求」，會持續整個 circuit（可能好幾小時），
    /// 不排除的話每個使用者離開頁面時都會記一筆慢請求。
    /// </summary>
    private static void LogIfSlowRequest(HttpContext context, ILogger requestLogger, TimeSpan elapsed)
    {
        if (IsLongLivedConnection(context.Request.Path))
        {
            return;
        }

        var threshold = context.RequestServices.GetService<IOptionsMonitor<SlowOperationSettings>>()?.CurrentValue.HttpRequestMs ?? 0;
        if (SlowOperationSettings.IsSlow(elapsed, threshold))
        {
            requestLogger.LogWarning(
                "Slow HTTP request. Method={Method}, Path={Path}, StatusCode={StatusCode}, ElapsedMilliseconds={ElapsedMilliseconds}, ThresholdMilliseconds={ThresholdMilliseconds}",
                context.Request.Method,
                context.Request.Path.Value,
                context.Response.StatusCode,
                (long)elapsed.TotalMilliseconds,
                threshold);
        }
    }

    internal static bool IsLongLivedConnection(PathString path)
        => path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 靜態資產的副檔名。單靠路徑前綴不夠 —— 例如 app.css 是掛在網站根目錄
    /// （/app.css）而非 /css 之下，只比對前綴會漏掉。
    /// </summary>
    private static readonly string[] StaticAssetExtensions =
    [
        ".css", ".js", ".map", ".png", ".jpg", ".jpeg", ".gif", ".svg",
        ".ico", ".woff", ".woff2", ".ttf", ".eot",
    ];

    private static bool IsLowValueRequest(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var prefix in LowValueRequestPrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var extension in StaticAssetExtensions)
        {
            if (value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>HttpContext.Items 的鍵：記住這個請求的追蹤碼，讓重新執行（/Error、/not-found）沿用同一個碼。</summary>
    private const string TraceCodeItemKey = "CustomerHelpAgent.TraceCode";

    public static WebApplication UseHttpRequestLogging<TProgram>(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var requestLogger = context.RequestServices.GetRequiredService<ILogger<TProgram>>();
            var stopwatch = Stopwatch.StartNew();

            // 錯誤追蹤碼（LOG-10）：取代 TraceIdentifier，API 回應的 TraceId、/Error 頁、日誌檔都是同一個碼。
            // ⚠️ UseExceptionHandler 與 UseStatusCodePagesWithReExecute 會拿同一個 HttpContext 重跑管線，
            // 再次經過這裡時必須沿用原本的碼，否則錯誤頁顯示的碼在日誌裡找不到。
            if (context.Items[TraceCodeItemKey] is not string traceCode)
            {
                traceCode = TraceCode.New();
                context.Items[TraceCodeItemKey] = traceCode;
                context.TraceIdentifier = traceCode;
            }

            using var traceScope = TraceCode.Begin(traceCode);

            // 供系統例外紀錄使用：這個請求內任何 LogError 都會帶上來源與路徑。
            // ⚠️ 這裡在 UseAuthentication 之前，User 還是匿名 —— 帳號由排在 UseAuthorization 之後的
            // UseExceptionContextUser 補上（內層看得到），本方法的 catch 則在記錄前自己重新取一次（外層看不到內層設定的值）。
            // 設定失敗只損失診斷資訊，不得影響請求本身，所以整段包 try/catch。
            try
            {
                var path = context.Request.Path.Value;
                var source = path is not null && path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
                    ? ExceptionSources.WebApi
                    : ExceptionSources.Ui;

                var (account, userId) = RequestActorResolver.Resolve(context.User);

                context.RequestServices.GetRequiredService<ExceptionContextAccessor>()
                    .Set(new ExceptionContext(source, path, account, userId));
            }
            catch (Exception ex)
            {
                // 診斷加值資訊，取不到就算了；但要留下痕跡（Warning 不會進例外紀錄，不會遞迴）。
                requestLogger.LogWarning(ex, "Failed to set exception context for HTTP request.");
            }

            try
            {
                await next();
                stopwatch.Stop();

                // 失敗的請求一律記在 Information 以上，即使路徑是低價值的 ——
                // 靜態資產 404 往往正是「連結壞掉」的線索。
                var isLowValue = IsLowValueRequest(context.Request.Path)
                    && context.Response.StatusCode < 400;

                requestLogger.Log(
                    isLowValue ? LogLevel.Debug : LogLevel.Information,
                    "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMilliseconds} ms",
                    context.Request.Method,
                    context.Request.Path.Value,
                    context.Response.StatusCode,
                    stopwatch.ElapsedMilliseconds);

                LogIfSlowRequest(context, requestLogger, stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                // 走到這裡時驗證已經跑過，User 有值了；但內層 UseExceptionContextUser 設定的帳號
                // 不會流回外層，所以記錄前在這裡補上，否則例外紀錄的帳號欄永遠是空的。
                EnrichExceptionContextUser(context);

                requestLogger.LogError(
                    ex,
                    "HTTP {Method} {Path} failed after {ElapsedMilliseconds} ms",
                    context.Request.Method,
                    context.Request.Path.Value,
                    stopwatch.ElapsedMilliseconds);
                throw;
            }
        });

        return app;
    }

    /// <summary>
    /// 把已驗證的使用者補進例外情境，讓內層（MVC 篩選器、控制器、服務）的 LogError 帶得出帳號。
    ///
    /// ⚠️ 必須排在 <c>UseAuthorization</c> 之後：JWT 不是預設驗證機制，
    /// JWT 端點的 User 要到授權中介軟體依原則驗證後才有值。
    /// </summary>
    public static WebApplication UseExceptionContextUser(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            EnrichExceptionContextUser(context);
            await next();
        });

        return app;
    }

    /// <summary>
    /// 補上已驗證的使用者與路由樣板。
    /// 只取帳號與 UserId —— 姓名、Email 屬個資，絕不進入紀錄。失敗只損失診斷資訊，絕不拋出。
    /// </summary>
    private static void EnrichExceptionContextUser(HttpContext context)
    {
        try
        {
            var accessor = context.RequestServices.GetRequiredService<ExceptionContextAccessor>();
            var current = accessor.Current;
            if (current is null)
            {
                return;
            }

            var enriched = current with { Page = ResolveRouteTemplate(context) ?? current.Page };

            var (account, userId) = RequestActorResolver.Resolve(context.User);
            if (userId is not null)
            {
                enriched = enriched with { Account = account, UserId = userId };
            }

            if (enriched != current)
            {
                accessor.Set(enriched);
            }
        }
        catch (Exception ex)
        {
            context.RequestServices.GetService<ILoggerFactory>()?
                .CreateLogger(typeof(ApplicationBuilderExtensions))
                .LogWarning(ex, "Failed to add the authenticated user to the exception context.");
        }
    }

    /// <summary>
    /// 路由樣板（LOG-16），例如 <c>/api/Category/{id}</c>。
    /// 例外紀錄的「頁面」是簽章的一部分：記原始路徑（<c>/api/Category/123</c>）會讓每個 Id 都變成一個新簽章，
    /// 同一個錯誤散成好幾列，還會吃掉 5000 列上限。還沒有路由結果（例如靜態檔）時回傳 null，沿用原始路徑。
    /// </summary>
    private static string? ResolveRouteTemplate(HttpContext context)
    {
        if (context.GetEndpoint() is not RouteEndpoint { RoutePattern.RawText: { Length: > 0 } rawText })
        {
            return null;
        }

        return rawText.StartsWith('/') ? rawText : "/" + rawText;
    }

    public static WebApplication UseConfiguredLocalization(this WebApplication app)
    {
        var localizationOptions = app.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<RequestLocalizationOptions>>()
            .Value;

        app.UseRequestLocalization(localizationOptions);
        return app;
    }

    /// <summary>下載目錄對應的外部路徑前綴。</summary>
    private const string DownloadRequestPath = "/UploadFiles";

    /// <summary>
    /// 把 <c>ExternalFileSystem.DownloadPath</c> 掛到 <c>/UploadFiles</c>。
    ///
    /// ⚠️ 必須在 <c>UseAuthentication()</c> / <c>UseAuthorization()</c> 之後呼叫，
    /// 且**要求已驗證身分**才提供檔案。
    ///
    /// 沿革：0.4.34 之前它掛在驗證中介軟體之前，且 <c>UseStaticFiles</c> 本身不看授權，
    /// 等於整個下載目錄匿名可讀。預設設定下 <c>ProjectFilePath</c> 不在 <c>DownloadPath</c>
    /// 底下所以沒有直接外洩，但交付到客戶端後只要有人把檔案放進該目錄就會裸奔 ——
    /// 與 <c>ProjectFileController</c> 的權限 + 團隊守門 + 稽核軌跡完全相反。
    /// </summary>
    public static WebApplication UseConfiguredDownloadStaticFiles(this WebApplication app, SystemSettings systemSettings)
    {
        if (string.IsNullOrWhiteSpace(systemSettings.ExternalFileSystem.DownloadPath))
        {
            return app;
        }

        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(DownloadRequestPath),
            branch =>
            {
                branch.Use(async (context, next) =>
                {
                    if (context.User?.Identity?.IsAuthenticated != true)
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }

                    await next();
                });

                branch.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(systemSettings.ExternalFileSystem.DownloadPath),
                    RequestPath = DownloadRequestPath
                });
            });

        return app;
    }
}
