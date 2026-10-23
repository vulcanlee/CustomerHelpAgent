using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.AI;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Web.Agents.AfterSales;

/// <summary>
/// 一輪 Agent 執行的工具環境（由 Runner 每輪建立）。
/// <see cref="CustomerUserId"/> 是會話擁有者，工具執行時仍以 <see cref="CurrentUserService"/> 的登入使用者為準，兩者不符即拒絕。
/// </summary>
public sealed class AfterSalesToolContext
{
    public required int CustomerUserId { get; init; }

    /// <summary>會話識別碼；也是 Agent 送出之售後申請的 <c>CaseKey</c>，以及照片暫存區的鍵。</summary>
    public required Guid SessionKey { get; init; }

    /// <summary>執行軌跡事件的出口。工具只發「工具內部的程式核對」事件（目前只有建立申請前的重核）。</summary>
    public Func<AgentTraceEvent, Task> Emit { get; init; } = _ => Task.CompletedTask;

    /// <summary>每次工具呼叫（含身分與權限檢查）的逾時；Runner 傳入 <c>AfterSalesAgentSettings.ToolTimeoutSeconds</c>。</summary>
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>售後申請建立並回查成功後呼叫（參數為申請 Id），供 Runner 回填確認紀錄。</summary>
    public Func<int, Task>? OnRequestCreated { get; init; }

    /// <summary>工具要求本輪停下的原因（<c>request_photo_upload</c> 成功時設為 <see cref="AgentStopReason.AwaitingPhotoUpload"/>）。</summary>
    public AgentStopReason? StopReason { get; set; }
}

/// <summary>
/// 售後 Agent 的 6 個受控工具。安全邊界：
/// <list type="bullet">
/// <item>顧客身分只來自 <see cref="CurrentUserService"/>（且須與 <see cref="AfterSalesToolContext.CustomerUserId"/> 相同、仍具「顧客商店」權限），
/// 工具參數沒有任何使用者識別欄位；模型多傳的引數會被 <see cref="AIFunctionFactory"/> 忽略。</item>
/// <item>回傳一律是 <see cref="ToolOutcome"/> 信封（序列化成 <see cref="System.Text.Json.JsonElement"/>），只含投影 DTO 的欄位。</item>
/// <item>寫入工具 <c>create_after_sales_request</c> 以 <see cref="ApprovalRequiredAIFunction"/> 包裝，執行時再以程式重核資格。</item>
/// </list>
/// 權限以 <see cref="IPermissionChecker"/> 即時查資料庫，而不是 <c>AuthenticationStateHelper.CheckAccessPage</c>：
/// 後者讀的是換頁時載入的 <c>CurrentUser.RoleList</c>，對話中途被收回權限要等下次換頁才會生效。
/// </summary>
public sealed class AfterSalesAgentTools
{
    public const string QueryMyOrdersName = "query_my_orders";
    public const string GetEffectivePolicyName = "get_effective_policy";
    public const string CheckVariantStockName = "check_variant_stock";
    public const string TrackShipmentName = "track_shipment";
    public const string RequestPhotoUploadName = "request_photo_upload";
    public const string CreateAfterSalesRequestName = "create_after_sales_request";

    private const string IssueTypeDescription =
        "問題類型：size_exchange（換尺碼）、color_exchange（換顏色）、return_refund（退貨退款）、quality_issue（品質問題）、shipment_urge（物流催件）";

    private const string OrderItemIdDescription = "訂單商品編號（query_my_orders 回傳的 order_item_id）";

    private static readonly string[] OrderWithheld = ["收件人", "地址", "手機", "價格"];
    private static readonly string[] StockWithheld = ["庫存數量"];
    private static readonly string[] ShipmentWithheld = ["收件人", "地址"];

    private static readonly Dictionary<string, AfterSalesType> IssueTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["size_exchange"] = AfterSalesType.SizeExchange,
        ["color_exchange"] = AfterSalesType.ColorExchange,
        ["return_refund"] = AfterSalesType.ReturnRefund,
        ["quality_issue"] = AfterSalesType.QualityIssue,
        ["shipment_urge"] = AfterSalesType.ShipmentUrge,
    };

    private readonly ShopOrderService shopOrderService;
    private readonly AfterSalesPolicyService policyService;
    private readonly AfterSalesRequestService requestService;
    private readonly IPermissionChecker permissionChecker;
    private readonly CurrentUserService currentUserService;
    private readonly AgentPhotoInbox photoInbox;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AfterSalesAgentTools> logger;
    private readonly AfterSalesToolContext context;

    public AfterSalesAgentTools(
        ShopOrderService shopOrderService,
        AfterSalesPolicyService policyService,
        AfterSalesRequestService requestService,
        IPermissionChecker permissionChecker,
        CurrentUserService currentUserService,
        AgentPhotoInbox photoInbox,
        TimeProvider timeProvider,
        ILogger<AfterSalesAgentTools> logger,
        AfterSalesToolContext context)
    {
        this.shopOrderService = shopOrderService;
        this.policyService = policyService;
        this.requestService = requestService;
        this.permissionChecker = permissionChecker;
        this.currentUserService = currentUserService;
        this.photoInbox = photoInbox;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.context = context;
    }

    /// <summary>
    /// 測試接縫：在身分檢查之後、服務呼叫之前執行，用來模擬緩慢的服務（逾時測試）。
    /// 服務方法沒有 <see cref="CancellationToken"/>，逾時是以 <c>WaitAsync</c> 放棄等待，不是中斷查詢。
    /// </summary>
    internal Func<CancellationToken, Task>? BeforeServiceCallForTesting { get; set; }

    internal const string SubmitStage = "submit";
    internal const string VerifyStage = "verify";

    /// <summary>
    /// 測試接縫：<c>create_after_sales_request</c> 在送件前（<see cref="SubmitStage"/>）與送件成功後回查前（<see cref="VerifyStage"/>）執行，
    /// 用來模擬送件拋例外、回查逾時。
    /// </summary>
    internal Func<string, CancellationToken, Task>? CreateStageHookForTesting { get; set; }

    /// <summary>模型送來的問題類型（<c>size_exchange</c>…）轉成 <see cref="AfterSalesType"/>；Runner 組確認卡時共用同一份對照。</summary>
    internal static bool TryParseIssueType(string? issueType, out AfterSalesType type)
        => IssueTypes.TryGetValue(issueType ?? string.Empty, out type);

    public IReadOnlyList<AIFunction> CreateFunctions() =>
    [
        Create(QueryMyOrdersAsync, QueryMyOrdersName,
            "查詢目前登入顧客本人的訂單商品（品名、顏色、尺碼、配送狀態）。可依顏色或關鍵字篩選；不提供收件人、地址、手機與價格。"),
        Create(GetEffectivePolicyAsync, GetEffectivePolicyName,
            "取得某個訂單商品在指定問題類型下目前生效的售後規則卡，並由程式計算簽收天數、不符原因與尚缺資訊。"),
        Create(CheckVariantStockAsync, CheckVariantStockName,
            "查詢訂單商品同款的各顏色／尺碼是否有貨（只回有無，不回數量）。"),
        Create(TrackShipmentAsync, TrackShipmentName,
            "查詢訂單商品的物流狀態與事件，並由程式判定是否可以催件。"),
        Create(RequestPhotoUploadAsync, RequestPhotoUploadName,
            "請顧客上傳照片（例如品質問題需要照片佐證）。呼叫後本輪會停下，等顧客上傳完成再繼續。"),
        new ApprovalRequiredAIFunction(Create(CreateAfterSalesRequestAsync, CreateAfterSalesRequestName,
            "建立售後申請。必須先取得規則卡並向顧客確認內容；顧客在確認卡上同意後才會執行，執行時程式會重新核對資格。")),
    ];

    public Task<ToolOutcome> QueryMyOrdersAsync(
        [Description("顏色（選填），例如：黑色")] string? color = null,
        [Description("品名或訂單編號關鍵字（選填）")] string? keyword = null,
        CancellationToken cancellationToken = default)
        => RunAsync(QueryMyOrdersName, async (customerUserId, ct) =>
        {
            var orders = await shopOrderService.GetMyOrdersAsync(customerUserId).WaitAsync(ct);
            var items = orders
                .SelectMany(o => o.Items.Select(i => new AgentOrderItem(i.OrderItemId, o.OrderNo, i.ProductName, i.Color, i.Size, o.StatusText)))
                .Where(x => MatchesColor(x.Color, color) && MatchesKeyword(x, keyword))
                .ToList();
            return items.Count == 0
                ? ToolOutcome.Fail(ToolOutcomeStatus.NotFound, "查無符合條件的訂單商品。")
                : ToolOutcome.Ok(items, OrderWithheld);
        }, cancellationToken);

    public Task<ToolOutcome> GetEffectivePolicyAsync(
        [Description(OrderItemIdDescription)] int orderItemId,
        [Description(IssueTypeDescription)] string issueType,
        CancellationToken cancellationToken = default)
        => RunAsync(GetEffectivePolicyName, async (customerUserId, ct) =>
        {
            if (!IssueTypes.TryGetValue(issueType ?? string.Empty, out var type))
            {
                return InvalidIssueType();
            }

            var target = await FindOwnedItemAsync(customerUserId, orderItemId, ct);
            if (target is null)
            {
                return ItemNotFound();
            }

            var (order, _) = target.Value;
            var shippedAt = type == AfterSalesType.ShipmentUrge ? await GetShippedAtAsync(customerUserId, order.OrderId, ct) : null;
            var now = Now();
            var (policy, failure) = await LookupPolicyAsync(type, target.Value.Item.ProductCategory, now, ct);
            if (policy is null)
            {
                return failure!;
            }

            var eligibility = AfterSalesEligibility.Evaluate(
                policy, order.DeliveredAt, isWorn: null, hasPhoto: photoInbox.Count(context.SessionKey) > 0, now, shippedAt);
            return ToolOutcome.Ok(new AgentPolicyCard(
                policy.Id,
                policy.Code,
                policy.Title,
                policy.Content,
                policy.Source,
                policy.Version,
                policy.EffectiveFrom,
                AfterSalesEligibility.DaysSinceDelivery(order.DeliveredAt, now),
                eligibility.UnmetReasons,
                eligibility.MissingInfo));
        }, cancellationToken);

    public Task<ToolOutcome> CheckVariantStockAsync(
        [Description(OrderItemIdDescription)] int orderItemId,
        CancellationToken cancellationToken = default)
        => RunAsync(CheckVariantStockName, async (customerUserId, ct) =>
        {
            var variants = await shopOrderService.GetVariantStockAsync(customerUserId, orderItemId).WaitAsync(ct);
            if (variants.Count == 0)
            {
                return ItemNotFound();
            }

            return ToolOutcome.Ok(variants.Select(x => new AgentStockOption(x.Color, x.Size, x.Stock > 0)).ToList(), StockWithheld);
        }, cancellationToken);

    public Task<ToolOutcome> TrackShipmentAsync(
        [Description(OrderItemIdDescription)] int orderItemId,
        CancellationToken cancellationToken = default)
        => RunAsync(TrackShipmentName, async (customerUserId, ct) =>
        {
            var target = await FindOwnedItemAsync(customerUserId, orderItemId, ct);
            if (target is null)
            {
                return ItemNotFound();
            }

            var order = target.Value.Order;
            var detail = await shopOrderService.GetMyOrderDetailAsync(customerUserId, order.OrderId).WaitAsync(ct);
            if (detail is null)
            {
                return ItemNotFound();
            }

            var shipment = detail.Shipment;
            if (shipment is null)
            {
                return ToolOutcome.Ok(new AgentShipment(string.Empty, order.StatusText, null, [], false), ShipmentWithheld);
            }

            var events = shipment.Events
                .Select(e => $"{e.OccurredAt.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)} {e.Location}：{e.Description}")
                .ToList();
            var canUrge = AfterSalesEligibility.CanRequestUrge(order.DeliveredAt, shipment.ShippedAt, Now());
            return ToolOutcome.Ok(new AgentShipment(shipment.Carrier, shipment.StatusText, shipment.ShippedAt, events, canUrge), ShipmentWithheld);
        }, cancellationToken);

    public Task<ToolOutcome> RequestPhotoUploadAsync(
        [Description(OrderItemIdDescription)] int orderItemId,
        [Description("要顧客上傳照片的原因，例如：請拍攝開膠或脫線處")] string reason,
        CancellationToken cancellationToken = default)
        => RunAsync(RequestPhotoUploadName, async (customerUserId, ct) =>
        {
            var item = await shopOrderService.GetMyOrderItemAsync(customerUserId, orderItemId).WaitAsync(ct);
            if (item is null)
            {
                return ItemNotFound();
            }

            context.StopReason = AgentStopReason.AwaitingPhotoUpload;
            return ToolOutcome.Ok(new
            {
                OrderItemId = item.OrderItemId,
                Reason = reason,
                UploadedCount = photoInbox.Count(context.SessionKey),
                MaxCount = AfterSalesRequestService.MaxFileCount,
            });
        }, cancellationToken);

    public Task<ToolOutcome> CreateAfterSalesRequestAsync(
        [Description(OrderItemIdDescription)] int orderItemId,
        [Description(IssueTypeDescription)] string issueType,
        [Description("問題描述（顧客的說法摘要）")] string description,
        [Description("換尺碼的目標尺碼（換尺碼時必填）")] string? targetSize = null,
        [Description("換顏色的目標顏色（換顏色時必填）")] string? targetColor = null,
        [Description("商品是否穿過；顧客沒說清楚就不要填")] bool? isWorn = null,
        CancellationToken cancellationToken = default)
        => RunAsync(CreateAfterSalesRequestName, async (customerUserId, ct) =>
        {
            if (!IssueTypes.TryGetValue(issueType ?? string.Empty, out var type))
            {
                return InvalidIssueType();
            }

            var target = await FindOwnedItemAsync(customerUserId, orderItemId, ct);
            if (target is null)
            {
                return ItemNotFound();
            }

            var (order, item) = target.Value;
            var shippedAt = type == AfterSalesType.ShipmentUrge ? await GetShippedAtAsync(customerUserId, order.OrderId, ct) : null;
            var now = Now();
            var (policy, failure) = await LookupPolicyAsync(type, item.ProductCategory, now, ct);
            if (policy is null)
            {
                return failure!;
            }

            // 確認卡顯示後到顧客按下同意之間，資料可能已變（例如超過期限、照片被清掉）：寫入前一律重核。
            var photoCount = photoInbox.Count(context.SessionKey);
            var eligibility = AfterSalesEligibility.Evaluate(policy, order.DeliveredAt, isWorn, photoCount > 0, now, shippedAt);
            if (!eligibility.IsEligible)
            {
                var reasons = eligibility.UnmetReasons.ToList();
                if (eligibility.MissingInfo.Count > 0)
                {
                    reasons.Add("尚缺：" + string.Join("、", eligibility.MissingInfo));
                }

                var reasonText = string.Join("；", reasons);
                await EmitProgramCheckAsync($"程式重新核對「{policy.Title}」規則資格不符（{reasonText}），不建立申請。");
                logger.LogInformation(
                    "Agent after-sales request blocked by eligibility re-check. OrderItemId={OrderItemId}, RequestType={RequestType}",
                    orderItemId, type);
                return new ToolOutcome(
                    ToolOutcomeStatus.Invalid,
                    new { eligibility.UnmetReasons, eligibility.MissingInfo },
                    $"程式重新核對後不符合申請條件：{reasonText}",
                    false,
                    []);
            }

            await EmitProgramCheckAsync(
                $"程式重新核對：登入身分與本人訂單相符，「{policy.Title}」規則（第 {policy.Version} 版）資格符合，附 {photoCount} 張照片送出申請。");

            // ⚠️ 送件一律等到完成，不受工具逾時與呼叫端取消約束：服務沒有 CancellationToken，放棄等待不會中斷寫入。
            // 逾時回報成可重試的 timeout 會讓模型重送；顧客關閉頁面（呼叫端取消）若拋出取消，申請已寫入卻不會回報編號、
            // 確認卡不會連到申請、照片也不會釋放。之後若續呼叫模型而被取消，由 Runner 以「申請已建立但 AI 回覆失敗」回報編號。
            var files = photoInbox.Take(context.SessionKey);
            SubmitResult result;
            try
            {
                if (CreateStageHookForTesting is { } submitHook)
                {
                    await submitHook(SubmitStage, CancellationToken.None);
                }

                result = await requestService.SubmitAsync(customerUserId, new SubmitAfterSalesInput
                {
                    CaseKey = context.SessionKey,
                    OrderItemId = item.OrderItemId,
                    Type = type,
                    TargetSize = targetSize,
                    TargetColor = targetColor,
                    IsWorn = isWorn,
                    Description = description ?? string.Empty,
                    Channel = AfterSalesChannel.Agent,
                }, files);
            }
            catch (Exception ex)
            {
                // 服務失敗時會刪掉這次已寫入的照片檔，所以照片放回暫存區。
                ReturnPhotos(files);
                logger.LogError(ex, "Agent after-sales submission threw. OrderItemId={OrderItemId}, RequestType={RequestType}", orderItemId, type);
                return ToolOutcome.Fail(ToolOutcomeStatus.Unavailable, "申請可能未完成，請到「我的申請」確認，請勿重複送出。");
            }

            if (!result.Success)
            {
                // 送件被拒（目標規格不存在、已有處理中的同類申請…）：照片放回暫存區，顧客不必重傳。
                ReturnPhotos(files);
                logger.LogInformation(
                    "Agent after-sales request rejected by submission. OrderItemId={OrderItemId}, RequestType={RequestType}",
                    orderItemId, type);
                return ToolOutcome.Fail(ToolOutcomeStatus.Invalid, result.Message);
            }

            DisposeFiles(files);
            await AfterCommitAsync(customerUserId, result.RequestId.GetValueOrDefault());
            logger.LogInformation(
                "Agent after-sales request created. RequestId={RequestId}, RequestType={RequestType}, PhotoCount={PhotoCount}",
                result.RequestId, type, files.Count);
            return ToolOutcome.Ok(new { result.RequestNo, result.RequestId });
        }, cancellationToken);

    private static AIFunction Create(Delegate method, string name, string description)
        => AIFunctionFactory.Create(method, new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description,
            ExcludeResultSchema = true,
            MarshalResult = (result, _, _) => new ValueTask<object?>(((ToolOutcome)result!).ToJsonElement()),
        });

    private static ToolOutcome ItemNotFound()
        => ToolOutcome.Fail(ToolOutcomeStatus.NotFound, "查無這個訂單商品（或不是您的訂單）。");

    private static ToolOutcome InvalidIssueType()
        => ToolOutcome.Fail(ToolOutcomeStatus.Invalid, "無法辨識的問題類型。" + IssueTypeDescription);

    /// <summary>「黑色」與「黑」視為同一個顏色。</summary>
    private static bool MatchesColor(string itemColor, string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return true;
        }

        var wanted = color.Trim().TrimEnd('色');
        return wanted.Length == 0
            || itemColor.Contains(wanted, StringComparison.Ordinal)
            || (itemColor.Length > 0 && wanted.Contains(itemColor, StringComparison.Ordinal));
    }

    private static bool MatchesKeyword(AgentOrderItem item, string? keyword)
        => string.IsNullOrWhiteSpace(keyword)
            || item.ProductName.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase)
            || item.OrderNo.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void DisposeFiles(IReadOnlyList<AfterSalesUploadFile> files)
    {
        foreach (var file in files)
        {
            file.Content.Dispose();
        }
    }

    private DateTime Now() => timeProvider.GetLocalNow().DateTime;

    /// <summary>
    /// 所有工具的共同外框：逾時（<see cref="AfterSalesToolContext.ToolTimeout"/>）→ 身分與權限 → 工具本體。
    /// 呼叫端取消（<paramref name="cancellationToken"/>）照常拋出 <see cref="OperationCanceledException"/>；其他例外 → <see cref="ToolOutcomeStatus.Unavailable"/>。
    /// </summary>
    private async Task<ToolOutcome> RunAsync(
        string toolName, Func<int, CancellationToken, Task<ToolOutcome>> body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(context.ToolTimeout);
        try
        {
            var customerUserId = await ResolveCustomerAsync(toolName).WaitAsync(timeout.Token);
            if (customerUserId is null)
            {
                return ToolOutcome.Fail(ToolOutcomeStatus.Forbidden, "登入狀態已失效或沒有使用權限。");
            }

            if (BeforeServiceCallForTesting is { } beforeServiceCall)
            {
                await beforeServiceCall(timeout.Token).WaitAsync(timeout.Token);
            }

            return await body(customerUserId.Value, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "After-sales agent tool timed out. ToolName={ToolName}, TimeoutMilliseconds={TimeoutMilliseconds}",
                toolName, context.ToolTimeout.TotalMilliseconds);
            return ToolOutcome.Fail(ToolOutcomeStatus.Timeout, "系統暫時沒有回應，目前無法確認（不代表查無資料）。", retryable: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "After-sales agent tool failed. ToolName={ToolName}", toolName);
            return ToolOutcome.Fail(ToolOutcomeStatus.Unavailable, "系統暫時無法使用，目前無法核實。");
        }
    }

    /// <summary>登入使用者 Id；未登入、不是會話擁有者，或已沒有「顧客商店」權限回 null。</summary>
    private async Task<int?> ResolveCustomerAsync(string toolName)
    {
        var userId = currentUserService.CurrentUser.Id;
        if (userId == 0 || userId != context.CustomerUserId)
        {
            logger.LogWarning(
                "After-sales agent tool rejected because the signed-in user does not own the session. ToolName={ToolName}, UserId={UserId}",
                toolName, userId);
            return null;
        }

        // 「頁面:view」動作鍵：裸頁面鍵（舊制＝全動作）也會通過，與 CheckAccessPage 的規則一致。
        if (!await permissionChecker.HasPermissionAsync(userId, PermissionKey.For(MagicObjectHelper.角色_顧客商店, PermissionActions.View)))
        {
            logger.LogWarning(
                "After-sales agent tool rejected because the user lacks the shop permission. ToolName={ToolName}, UserId={UserId}",
                toolName, userId);
            return null;
        }

        return userId;
    }

    /// <summary>本人的訂單商品與所屬訂單（簽收時間、配送狀態）；不是本人的回 null。</summary>
    private async Task<(ShopOrderSummary Order, ShopOrderItemView Item)?> FindOwnedItemAsync(
        int customerUserId, int orderItemId, CancellationToken ct)
    {
        var orders = await shopOrderService.GetMyOrdersAsync(customerUserId).WaitAsync(ct);
        foreach (var order in orders)
        {
            var item = order.Items.FirstOrDefault(x => x.OrderItemId == orderItemId);
            if (item is not null)
            {
                return (order, item);
            }
        }

        logger.LogInformation("Agent tool order item not found for customer. OrderItemId={OrderItemId}", orderItemId);
        return null;
    }

    private async Task<DateTime?> GetShippedAtAsync(int customerUserId, int orderId, CancellationToken ct)
        => (await shopOrderService.GetMyOrderDetailAsync(customerUserId, orderId).WaitAsync(ct))?.Shipment?.ShippedAt;

    /// <summary>唯一生效的規則；查無或衝突時回對應的失敗信封。</summary>
    private async Task<(AfterSalesPolicyModel? Policy, ToolOutcome? Failure)> LookupPolicyAsync(
        AfterSalesType type, string productCategory, DateTime now, CancellationToken ct)
    {
        var lookup = await policyService.GetEffectiveAsync(type, productCategory, now).WaitAsync(ct);
        return lookup.Status switch
        {
            PolicyLookupStatus.Found => (lookup.Policies[0], null),
            PolicyLookupStatus.Conflict => (null, ToolOutcome.Fail(
                ToolOutcomeStatus.Conflict, "這類問題同時有多條生效的規則，無法自動判定，請轉人工客服。")),
            _ => (null, ToolOutcome.Fail(ToolOutcomeStatus.NoPolicy, "查無這類問題適用的售後規則，請轉人工客服。")),
        };
    }

    /// <summary>
    /// 申請已寫入之後的收尾：回查（自己的逾時）與 <see cref="AfterSalesToolContext.OnRequestCreated"/>。
    /// ⚠️ 這裡的任何失敗（含逾時）只記 Warning，不改變結果：申請已經存在，回報失敗會讓模型或顧客重送。
    /// 不綁呼叫端的取消：顧客關閉頁面時仍要把確認卡連到申請，回查則以自己的逾時為限。
    /// </summary>
    private async Task AfterCommitAsync(int customerUserId, int requestId)
    {
        using (var verifyTimeout = new CancellationTokenSource(context.ToolTimeout))
        {
            try
            {
                if (CreateStageHookForTesting is { } verifyHook)
                {
                    await verifyHook(VerifyStage, verifyTimeout.Token).WaitAsync(verifyTimeout.Token);
                }

                var mine = await requestService.GetMyRequestsAsync(customerUserId).WaitAsync(verifyTimeout.Token);
                if (mine.All(x => x.Id != requestId))
                {
                    logger.LogWarning("Agent after-sales request could not be verified after submission. RequestId={RequestId}", requestId);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Agent after-sales request verification failed after submission. RequestId={RequestId}", requestId);
            }
        }

        if (context.OnRequestCreated is not { } onCreated)
        {
            return;
        }

        try
        {
            await onCreated(requestId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Agent after-sales request-created hook failed. RequestId={RequestId}", requestId);
        }
    }

    private Task EmitProgramCheckAsync(string plainText)
        => context.Emit(new AgentTraceEvent(AgentStepKind.ProgramCheck, AgentDataSource.Program, plainText, null, Now()));

    private void ReturnPhotos(IReadOnlyList<AfterSalesUploadFile> files)
    {
        foreach (var file in files)
        {
            photoInbox.Add(context.SessionKey, file.FileName, ((MemoryStream)file.Content).ToArray());
            file.Content.Dispose();
        }
    }
}
