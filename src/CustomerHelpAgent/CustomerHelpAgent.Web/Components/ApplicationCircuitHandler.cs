using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Server.Circuits;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Auth;
using CustomerHelpAgent.Web.Diagnostics;

namespace CustomerHelpAgent.Web.Components;

/// <summary>
/// 紀錄 Blazor Server circuit 生命週期（開啟 / 連線起落 / 關閉）與**使用者的畫面切換軌跡**。
///
/// 導覽日誌集中在這裡而非 MainLayout，是因為 CircuitHandler 涵蓋所有版面 ——
/// 包含繞過 MainLayout 的 EmptyLayout（啟動畫面）與 NoFooterLayout（登入頁）。
/// 這樣不需要動到任何頁面就能追蹤完整的使用者軌跡。
///
/// 身分取自 AuthenticationStateProvider 而**不是** CurrentUserService：後者要等
/// AuthenticationStateHelper.Check() 在元件的 OnInitializedAsync 跑過才有值，而首頁走
/// EmptyLayout 根本不會跑到，會導致大部分 circuit 都記成匿名。CircuitFactory 的順序是
/// 建立 scope → 初始化 NavigationManager → 設定 AuthenticationState → 才解析 CircuitHandler，
/// 所以這裡取得的驗證狀態是可靠的。
///
/// **注意 claim 對應在兩套驗證機制中是相反的**：
///   Cookie（本頁適用）：Sid=UserId、NameIdentifier=Account、Name=**姓名（個資，絕不記錄）**
///   JWT（API 適用）：  NameIdentifier=UserId、Name=Account
/// 在此誤用 ClaimTypes.Name 會把使用者姓名寫進日誌。
///
/// 稽核紀錄的來源 IP（0.9.117 起）也在這裡取得：circuit 內的元件事件沒有可靠的 HttpContext，
/// 只有連線建立（與斷線重連）的當下能從 SignalR 連線拿到 IP，存進同一個 scope 的 <see cref="ClientIpProvider"/>。
///
/// 開頁稽核（0.9.131 起）也集中在這裡：已登入的使用者每換一頁寫一筆 <c>Page.View</c>，各頁面不用改。
/// </summary>
public sealed class ApplicationCircuitHandler : CircuitHandler, IDisposable
{
    private const string AnonymousAccount = "(anonymous)";

    private readonly ILogger<ApplicationCircuitHandler> logger;
    private readonly NavigationManager navigationManager;
    private readonly AuthenticationStateProvider authenticationStateProvider;
    private readonly ExceptionContextAccessor exceptionContextAccessor;
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly ClientIpProvider clientIpProvider;
    private readonly IAuditLogService auditLogService;

    private string circuitId = string.Empty;
    private string previousPath = string.Empty;
    private string account = AnonymousAccount;
    private int userId;
    private bool subscribed;
    private bool entryAudited;

    public ApplicationCircuitHandler(
        ILogger<ApplicationCircuitHandler> logger,
        NavigationManager navigationManager,
        AuthenticationStateProvider authenticationStateProvider,
        ExceptionContextAccessor exceptionContextAccessor,
        IHttpContextAccessor httpContextAccessor,
        ClientIpProvider clientIpProvider,
        IAuditLogService auditLogService)
    {
        this.logger = logger;
        this.navigationManager = navigationManager;
        this.authenticationStateProvider = authenticationStateProvider;
        this.exceptionContextAccessor = exceptionContextAccessor;
        this.httpContextAccessor = httpContextAccessor;
        this.clientIpProvider = clientIpProvider;
        this.auditLogService = auditLogService;
    }

    /// <summary>
    /// 在每一次 inbound circuit activity（按鈕點擊、表單送出、元件生命週期回呼）之前，
    /// 把「誰、在哪一頁」放進 ambient context，好讓該次活動中任何
    /// <c>logger.LogError(ex, …)</c> 都能被系統例外紀錄帶上使用者與頁面。
    ///
    /// 這是唯一乾淨的掛點：服務層那 75 個 catch 只知道自己是哪個模組，
    /// 不可能知道呼叫者是誰。在這裡設定一次，整條呼叫鏈都讀得到。
    ///
    /// ⚠️ <b>全站每一次互動都會經過這裡。</b>因此內部全程 try/catch ——
    /// 記錄用的情境設定失敗，絕不可以連帶弄壞使用者正在做的事。
    /// </summary>
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
    {
        return async context =>
        {
            try
            {
                exceptionContextAccessor.Set(
                    new ExceptionContext(ExceptionSources.Ui, previousPath, account, userId));
            }
            catch (Exception ex)
            {
                // 情境只是診斷用的加值資訊，設定失敗就讓它空著，不影響使用者操作；
                // 但要留下痕跡（Warning 不會進例外紀錄，不會遞迴）。
                logger.LogWarning(ex, "Failed to set exception context for circuit activity. CircuitId={CircuitId}", circuitId);
            }

            // 每次互動一個錯誤追蹤碼（LOG-10）。circuit 內沒有 HttpContext，
            // 日誌檔的 TraceId 欄位原本是空的或過期的，一次操作的多行日誌串不起來。
            using var traceScope = TraceCode.Begin(TraceCode.New());

            await next(context);
        };
    }

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        circuitId = circuit.Id;
        previousPath = ToRelativePath(navigationManager.Uri);
        await ResolveIdentityAsync();

        navigationManager.LocationChanged += OnLocationChanged;
        subscribed = true;

        // 進入點記在這裡：circuit 開啟時第一個頁面已經載入，不會再觸發 LocationChanged。
        logger.LogInformation(
            "Blazor circuit opened. CircuitId={CircuitId}, EntryPath={EntryPath}, Account={Account}, UserId={UserId}",
            circuitId, previousPath, account, userId);
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        try
        {
            clientIpProvider.SetCircuitClientIp(httpContextAccessor.HttpContext?.Connection.RemoteIpAddress);
        }
        catch (Exception ex)
        {
            // 取不到 IP 只會讓稽核紀錄的來源 IP 空著，不可以弄壞連線。
            logger.LogWarning(ex, "Could not resolve client IP for circuit. CircuitId={CircuitId}", circuit.Id);
        }

        var (account, userId) = ResolveUser();
        logger.LogDebug(
            "Blazor circuit connection up. CircuitId={CircuitId}, Account={Account}, UserId={UserId}",
            circuit.Id, account, userId);

        // 進入的第一頁不會觸發 LocationChanged。等到第一次連線建立才寫：這時才拿得到 circuit 的來源 IP
        // （Blazor 的順序是先 Opened 再 ConnectionUp）。斷線重連會再進來一次，所以只寫一次。
        if (entryAudited == false)
        {
            entryAudited = true;
            AuditPageView(previousPath, "entry");
        }

        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var (account, userId) = ResolveUser();
        logger.LogWarning(
            "Blazor circuit connection down (client may have disconnected). CircuitId={CircuitId}, Account={Account}, UserId={UserId}, LastPath={LastPath}",
            circuit.Id, account, userId, previousPath);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Unsubscribe();

        var (account, userId) = ResolveUser();
        logger.LogInformation(
            "Blazor circuit closed. CircuitId={CircuitId}, Account={Account}, UserId={UserId}, LastPath={LastPath}",
            circuit.Id, account, userId, previousPath);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 使用者切換畫面。這是「使用者當時做了什麼」最基本的一條軌跡，因此記在 Information。
    /// </summary>
    private void OnLocationChanged(object? sender, LocationChangedEventArgs args)
    {
        var path = ToRelativePath(args.Location);
        var (account, userId) = ResolveUser();

        logger.LogInformation(
            "User navigated. Path={Path}, PreviousPath={PreviousPath}, Account={Account}, UserId={UserId}, CircuitId={CircuitId}",
            path, previousPath, account, userId, circuitId);

        // 只改 query string（同一頁換篩選條件）不算換頁。
        if (path != previousPath)
        {
            AuditPageView(path, previousPath);
        }

        previousPath = path;
    }

    /// <summary>
    /// 開頁稽核（0.9.131 起）：只記已登入的使用者；路徑已去掉 query string。
    ///
    /// 射後不理：換頁事件是同步的，而且不能讓寫資料庫拖慢換頁。<see cref="IAuditLogService.WriteAsync"/> 不拋出、
    /// 每次各用一個 DbContext，不會和新頁面初始化撞在同一個 context 上。
    /// </summary>
    private void AuditPageView(string path, string from)
    {
        if (userId <= 0)
        {
            return;
        }

        try
        {
            _ = auditLogService.WriteAsync(
                AuditActions.Page.View, success: true, actorUserId: userId, actorAccount: account,
                targetType: "Page", targetId: path, detail: $"from={from}");
        }
        catch (Exception ex)
        {
            // 全站每次換頁都經過這裡：稽核失敗不可以弄壞換頁。
            logger.LogWarning(ex, "Failed to start page view audit. Path={Path}, CircuitId={CircuitId}", path, circuitId);
        }
    }

    /// <summary>
    /// 只取 Account 與 UserId —— 姓名、Email 等個資不進日誌。
    /// 註：Google SSO 使用者的 Account 本身就是 Email，這是已知且已接受的取捨。
    /// </summary>
    private (string Account, int UserId) ResolveUser() => (account, userId);

    private async Task ResolveIdentityAsync()
    {
        try
        {
            var state = await authenticationStateProvider.GetAuthenticationStateAsync();
            var principal = state.User;
            if (principal.Identity?.IsAuthenticated != true)
            {
                return;
            }

            // Cookie 驗證的對應：NameIdentifier=帳號、Sid=使用者編號。
            // ClaimTypes.Name 在這裡是「姓名」，屬個資，刻意不取用。
            account = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? AnonymousAccount;
            userId = int.TryParse(principal.FindFirstValue(ClaimTypes.Sid), out var id) ? id : 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve identity for circuit. CircuitId={CircuitId}", circuitId);
        }
    }

    /// <summary>只記路徑，不記 query string —— 它可能挾帶使用者輸入或敏感參數。</summary>
    private string ToRelativePath(string uri)
    {
        try
        {
            var relative = navigationManager.ToBaseRelativePath(uri);
            var queryIndex = relative.IndexOf('?');
            if (queryIndex >= 0)
            {
                relative = relative[..queryIndex];
            }

            return string.IsNullOrEmpty(relative) ? "/" : "/" + relative;
        }
        catch (ArgumentException)
        {
            // ToBaseRelativePath 對不屬於本站的 URI 會丟例外；導覽日誌不該因此中斷。
            return "(external)";
        }
    }

    private void Unsubscribe()
    {
        if (subscribed == false)
        {
            return;
        }

        navigationManager.LocationChanged -= OnLocationChanged;
        subscribed = false;
    }

    /// <summary>
    /// 解除事件訂閱。CircuitHandler 是 Scoped、生命週期等同 circuit，但仍必須解除 ——
    /// LogLevelRuntimeState 先前就是因為漏了這步而造成跨測試污染。
    /// </summary>
    public void Dispose() => Unsubscribe();
}
