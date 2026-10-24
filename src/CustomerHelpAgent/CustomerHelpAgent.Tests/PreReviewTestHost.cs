using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents;
using CustomerHelpAgent.Web.Agents.AfterSales;
using CustomerHelpAgent.Web.Agents.PreReview;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Diagnostics;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// AI 初審背景執行與決策協調的測試主機：真正的 DI 容器（每個 scope 各自一份 scoped 服務，<see cref="CurrentUserService"/> 預設空白），
/// 檔案型 SQLite（併行節點各自開連線）、固定時鐘。AI 節點預設是 <see cref="PreReviewFakes"/>，也可換成真正的模型節點（腳本化模型）。
/// </summary>
internal sealed class PreReviewTestHost : IAsyncDisposable
{
    // UTC 02:00 = 本地（UTC+8）10:00
    public static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);
    public static readonly DateTime LocalNow = new(2026, 10, 23, 10, 0, 0);
    public static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

    private readonly string dbPath = Path.Combine(Path.GetTempPath(), "PreReviewHost-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly string fileRoot = Path.Combine(Path.GetTempPath(), "PreReviewHostFiles-" + Guid.NewGuid().ToString("N"));
    private readonly List<IServiceScope> scopes = [];

    private PreReviewTestHost()
    {
        Factory = new GatedDbContextFactory(dbPath);
    }

    public GatedDbContextFactory Factory { get; }

    public ServiceProvider Services { get; private set; } = null!;

    public RecordingAuditLogService Audit { get; } = new();

    public GatedNotificationSender Sender { get; } = new();

    public PreReviewFakes Fakes { get; } = new();

    public RecordingTokenUsageRecorder Usage { get; } = new();

    public FakeAiCallLogRecorder CallLog { get; } = new();

    public ManualTimeProvider Time { get; } = new(UtcNow);

    public PreReviewQueue Queue => Services.GetRequiredService<PreReviewQueue>();

    /// <summary>容器內所有 logger 寫出的 Error 以上紀錄（類別與訊息）。</summary>
    public ErrorLogCollector Errors { get; } = new();

    public int Alice { get; private set; }

    public int ProductId { get; private set; }

    /// <param name="model">給了就用真正的三個模型節點（模型換成這個腳本化 client）；null 用 <see cref="Fakes"/>。</param>
    /// <param name="configure">最後套用的覆寫（後註冊者勝出）。</param>
    public static async Task<PreReviewTestHost> CreateAsync(IChatClient? model = null, Action<IServiceCollection>? configure = null)
    {
        var host = new PreReviewTestHost();
        Directory.CreateDirectory(host.fileRoot);
        await host.SeedAsync();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(host.Errors));
        services.AddSingleton<IDbContextFactory<BackendDBContext>>(host.Factory);
        services.AddSingleton<TimeProvider>(host.Time);
        services.AddSingleton<IAuditLogService>(host.Audit);
        services.AddSingleton<INotificationSender>(host.Sender);
        services.AddSingleton(TestDemoControl.Off(host.Factory));
        var systemSettings = new SystemSettings();
        systemSettings.ExternalFileSystem.AfterSalesFilePath = host.fileRoot;
        services.AddSingleton(Options.Create(systemSettings));
        services.AddSingleton<ExceptionContextAccessor>();
        services.AddScoped<CurrentUserService>();
        services.AddScoped<AfterSalesPolicyService>();
        services.AddScoped<ShopOrderService>();
        services.AddScoped<AfterSalesFileStore>();
        services.AddScoped<AfterSalesDecisionService>();
        services.AddScoped<AfterSalesRequestService>();
        services.AddScoped<PreReviewRunService>();
        services.AddScoped<WorkflowCheckpointService>();
        services.AddSingleton<PreReviewSignal>();
        services.AddScoped<PreReviewWorkflowFactory>();
        services.AddScoped<PreReviewWorkflowRunner>();
        services.AddSingleton<PreReviewQueue>();
        services.AddScoped<IAfterSalesSubmittedHook, PreReviewSubmittedHook>();
        services.AddScoped<AfterSalesReviewCoordinator>();
        services.AddSingleton<PreReviewWorker>();

        if (model is null)
        {
            services.AddScoped<IPhotoReviewer>(_ => new FakePhotoReviewer(host.Fakes));
            services.AddScoped<IHistoryReviewer>(_ => new FakeHistoryReviewer(host.Fakes));
            services.AddScoped<IPreReviewSummarizer>(_ => new FakeSummarizer(host.Fakes));
        }
        else
        {
            // 真正的模型節點：provider → AgentChatClientFactory（Token 用量、AI 對話紀錄）→ 腳本化模型。
            services.AddScoped(sp => new AgentChatClientFactory(
                new StaticOptionsMonitor<AiSettings>(new AiSettings
                {
                    Provider = nameof(AiProvider.AzureOpenAI),
                    Endpoint = "https://unit-test.openai.azure.com",
                    ApiKey = "key",
                    Model = "my-deployment",
                }),
                new StaticOptionsMonitor<SlowOperationSettings>(new SlowOperationSettings()),
                host.Usage,
                host.CallLog,
                new StubAiQuotaService(),
                host.Audit,
                sp.GetRequiredService<CurrentUserService>(),
                NullLoggerFactory.Instance)
            {
                InnerClientFactory = (_, _) => model,
            });
            services.AddSingleton<IAiSystemPromptProvider>(new StubSystemPromptProvider());
            services.AddScoped<AgentRecordingService>();
            services.AddScoped<AgentTraceService>();
            services.AddSingleton<ReplayCursorStore>();
            services.AddSingleton<IOptionsMonitor<AfterSalesAgentSettings>>(new StaticOptionsMonitor<AfterSalesAgentSettings>(new AfterSalesAgentSettings()));
            services.AddScoped<PreReviewChatClientProvider>();
            services.AddScoped<IPhotoReviewer, ModelPhotoReviewer>();
            services.AddScoped<IHistoryReviewer, ModelHistoryReviewer>();
            services.AddScoped<IPreReviewSummarizer, ModelPreReviewSummarizer>();
        }

        configure?.Invoke(services);
        host.Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return host;
    }

    /// <summary>新的 scope（測試結束時一併釋放）；給了使用者就設為目前使用者（客服或顧客的 circuit）。</summary>
    public IServiceProvider Scope(int? userId = null, string? account = null)
    {
        var scope = Services.CreateScope();
        scopes.Add(scope);
        if (userId is { } id)
        {
            scope.ServiceProvider.GetRequiredService<CurrentUserService>().CurrentUser = new CurrentUser { Id = id, Account = account ?? $"user{id}" };
        }

        return scope.ServiceProvider;
    }

    /// <summary>客服 <paramref name="staffUserId"/> 的 circuit 裡的協調器。</summary>
    public AfterSalesReviewCoordinator Coordinator(int staffUserId)
        => Scope(staffUserId, $"agent{staffUserId}").GetRequiredService<AfterSalesReviewCoordinator>();

    public PreReviewRunService Runs => Scope().GetRequiredService<PreReviewRunService>();

    public PreReviewWorker Worker => Services.GetRequiredService<PreReviewWorker>();

    /// <summary>一筆訂單（簽收 2 天）＋一筆已提交的換尺碼申請（換 27 號，有庫存）。</summary>
    public async Task<int> AddRequestAsync(
        AfterSalesChannel channel = AfterSalesChannel.Agent,
        AfterSalesStatus status = AfterSalesStatus.Submitted,
        string targetSize = "27")
    {
        await using var ctx = Factory.CreateDbContext();
        var item = await AddOrderItemAsync(ctx);
        var row = new AfterSalesRequest
        {
            RequestNo = Guid.NewGuid().ToString("N"),
            CaseKey = Guid.NewGuid(),
            CustomerUserId = Alice,
            SalesOrderItemId = item.Id,
            Type = AfterSalesType.SizeExchange,
            Status = status,
            Channel = channel,
            TargetSize = targetSize,
            IsWorn = false,
            Description = "尺碼太小",
            CreatedAt = LocalNow,
            UpdatedAt = LocalNow,
        };
        ctx.AfterSalesRequest.Add(row);
        await ctx.SaveChangesAsync();
        row.RequestNo = $"AS20261023{row.Id:D4}";
        await ctx.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>只建訂單（顧客自己送件用），回傳訂單商品 Id。</summary>
    public async Task<int> AddOrderItemAsync()
    {
        await using var ctx = Factory.CreateDbContext();
        return (await AddOrderItemAsync(ctx)).Id;
    }

    public async Task<AfterSalesRequest> LoadRequestAsync(int requestId)
    {
        await using var ctx = Factory.CreateDbContext();
        return await ctx.AfterSalesRequest.AsNoTracking().SingleAsync(x => x.Id == requestId);
    }

    public async Task<int> FulfilmentCountAsync(int requestId)
    {
        await using var ctx = Factory.CreateDbContext();
        return await ctx.AfterSalesFulfillment.CountAsync(x => x.AfterSalesRequestId == requestId);
    }

    public async Task<int> StockAsync(string size)
    {
        await using var ctx = Factory.CreateDbContext();
        return (await ctx.ProductVariant.SingleAsync(x => x.ProductId == ProductId && x.Size == size)).Stock;
    }

    /// <summary>這一輪 Workflow 會話目前存著幾筆 checkpoint。</summary>
    public async Task<int> CheckpointCountAsync(int runId)
    {
        await using var ctx = Factory.CreateDbContext();
        var sessionId = await ctx.PreReviewRun.Where(x => x.Id == runId).Select(x => x.WorkflowSessionId).SingleAsync();
        return await ctx.WorkflowCheckpoint.CountAsync(x => x.SessionId == sessionId);
    }

    /// <summary>這筆申請最新一輪（含節點）。</summary>
    public async Task<PreReviewRun> LatestRunAsync(int requestId)
        => await Runs.GetLatestForRequestAsync(requestId) ?? throw new InvalidOperationException("no run");

    public async Task<List<PreReviewRun>> RunsOfAsync(int requestId)
    {
        await using var ctx = Factory.CreateDbContext();
        return await ctx.PreReviewRun.AsNoTracking().Where(x => x.AfterSalesRequestId == requestId).OrderBy(x => x.Attempt).ToListAsync();
    }

    /// <summary>佇列裡目前的 Id（全部取出）。</summary>
    public List<int> DrainQueue()
    {
        var ids = new List<int>();
        while (Queue.Reader.TryRead(out var id))
        {
            ids.Add(id);
        }

        return ids;
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(50);
        }
    }

    public static NodeStatus? NodeOf(PreReviewRun run, PreReviewNode node)
        => run.Nodes.SingleOrDefault(x => x.Node == node)?.Status;

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in scopes)
        {
            scope.Dispose();
        }

        if (Services is not null)
        {
            await Services.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm", dbPath + "-journal" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        if (Directory.Exists(fileRoot))
        {
            Directory.Delete(fileRoot, recursive: true);
        }
    }

    private async Task SeedAsync()
    {
        await using var ctx = Factory.CreateDbContext();
        await ctx.Database.EnsureCreatedAsync();
        var user = new MyUser { Account = "alice", Password = "x", Name = "Alice" };
        ctx.MyUser.Add(user);
        var product = new Product { Code = "P1", Name = "跑鞋", Category = ProductCategories.Shoes, Price = 1999m };
        ctx.ProductVariant.AddRange(
            new ProductVariant { Product = product, Color = "黑", Size = "26", Stock = 5 },
            new ProductVariant { Product = product, Color = "黑", Size = "27", Stock = 2 },
            new ProductVariant { Product = product, Color = "黑", Size = "28", Stock = 0 });
        ctx.AfterSalesPolicy.Add(new AfterSalesPolicy
        {
            Code = "EX-SIZE",
            Type = AfterSalesType.SizeExchange,
            Category = ProductCategories.Shoes,
            Title = "鞋類換尺碼",
            Content = "簽收 7 天內、未穿著可換尺碼。",
            Source = "售後規則",
            Version = 1,
            EffectiveFrom = LocalNow.AddDays(-100),
            WindowDays = 7,
            RequiresUnworn = true,
            RequiresPhoto = false,
            CreatedAt = LocalNow.AddDays(-100),
        });
        await ctx.SaveChangesAsync();
        Alice = user.Id;
        ProductId = product.Id;
    }

    private async Task<SalesOrderItem> AddOrderItemAsync(BackendDBContext ctx)
    {
        var variant26 = await ctx.ProductVariant.SingleAsync(x => x.ProductId == ProductId && x.Size == "26");
        var order = new SalesOrder
        {
            OrderNo = "A" + Guid.NewGuid().ToString("N")[..8],
            CustomerUserId = Alice,
            OrderedAt = LocalNow.AddDays(-5),
            DeliveredAt = LocalNow.AddDays(-2),
            RecipientName = "王小明",
            ShippingAddress = "台北市某路1號",
            RecipientPhone = "0912345678",
            Items = [new SalesOrderItem { ProductVariantId = variant26.Id, Quantity = 1, UnitPrice = 1999m }],
        };
        ctx.SalesOrder.Add(order);
        await ctx.SaveChangesAsync();
        return order.Items[0];
    }
}

/// <summary>
/// 檔案型 SQLite 的 context 工廠，依「誰在開連線」（呼叫堆疊上的型別＋方法，精確比對）做三件事：
/// 卡住或弄壞下一次由指定方法發出的開連線（製造當機）、數 <see cref="AfterSalesDecisionService.ApplyAsync"/> 的次數、
/// 讓指定方法的多個呼叫在開連線時互相等待（製造真正的併發）。
/// ⚠️ 前提：從被比對的方法到 <c>CreateDbContextAsync</c> 必須是同步的一段呼叫鏈 —— 也就是被比對的方法在「自己下一個真正非同步的 await」之前
/// 就開連線（<c>ApplyAsync</c>、<c>TryClaimDecisionAsync</c> 的第一個 await 就是開連線）。await 恢復後的堆疊只剩恢復中的那個方法，
/// 看不到更外層的呼叫者；所以 <see cref="WorkflowApplyCalls"/> 比對的是直接呼叫 <c>ApplyAsync</c> 的 <c>ExecuteExecutor.RunNodeAsync</c>。
/// 被比對的方法改名或調整 await 順序時，相關測試會失效。
/// </summary>
internal sealed class GatedDbContextFactory(string path) : IDbContextFactory<BackendDBContext>
{
    private Func<Task>? gate;
    private (Type Type, string Method)? gateTarget;
    private int applyCalls;
    private int workflowApplyCalls;
    private readonly List<Barrier> barriers = [];

    /// <summary><see cref="AfterSalesDecisionService.ApplyAsync"/> 被呼叫的次數（直接決定與 Workflow 執行節點合計）。</summary>
    public int ApplyCalls => Volatile.Read(ref applyCalls);

    /// <summary>由 Workflow 執行節點（<see cref="ExecuteExecutor"/>）發出的 <c>ApplyAsync</c> 次數：決定被送進 Workflow 執行了幾次。</summary>
    public int WorkflowApplyCalls => Volatile.Read(ref workflowApplyCalls);

    /// <summary>
    /// 由 <paramref name="type"/>.<paramref name="method"/> 開連線的前 <paramref name="participants"/> 個呼叫互相等待、到齊才一起放行
    /// （等到 <paramref name="timeout"/> 也放行，預設 5 秒），用來讓兩位客服「同時」走到同一步，製造真正的競爭。可同時設多個。
    /// </summary>
    public void ArmBarrier(Type type, string method, int participants, TimeSpan? timeout = null)
    {
        lock (barriers)
        {
            barriers.Add(new Barrier(type, method, participants, timeout ?? TimeSpan.FromSeconds(5)));
        }
    }

    /// <summary>下一次由 <paramref name="type"/>.<paramref name="method"/> 開連線時先 await <paramref name="wait"/>（只觸發一次；拋例外就是讓那次開連線失敗）。</summary>
    public void Arm(Type type, string method, Func<Task> wait)
    {
        gateTarget = (type, method);
        Volatile.Write(ref gate, wait);
    }

    public BackendDBContext CreateDbContext()
        => new(new DbContextOptionsBuilder<BackendDBContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .Options);

    public async Task<BackendDBContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        var frames = new StackTrace().GetFrames();
        if (OnStack(frames, typeof(AfterSalesDecisionService), nameof(AfterSalesDecisionService.ApplyAsync)))
        {
            Interlocked.Increment(ref applyCalls);
            if (OnStack(frames, typeof(ExecuteExecutor), "RunNodeAsync"))
            {
                Interlocked.Increment(ref workflowApplyCalls);
            }
        }

        Barrier[] armed;
        lock (barriers)
        {
            armed = barriers.ToArray();
        }

        foreach (var entry in armed.Where(x => OnStack(frames, x.Type, x.Method)))
        {
            var remaining = Interlocked.Decrement(ref entry.Remaining);
            if (remaining == 0)
            {
                entry.Released.TrySetResult();
            }

            if (remaining >= 0)
            {
                await entry.Released.Task.WaitAsync(entry.Timeout).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }

        if (Volatile.Read(ref gate) is not null
            && gateTarget is { } target
            && OnStack(frames, target.Type, target.Method)
            && Interlocked.Exchange(ref gate, null) is { } wait)
        {
            await wait();
        }

        return CreateDbContext();
    }

    private sealed class Barrier(Type type, string method, int participants, TimeSpan timeout)
    {
        public int Remaining = participants;

        public Type Type { get; } = type;

        public string Method { get; } = method;

        public TimeSpan Timeout { get; } = timeout;

        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// 堆疊上有沒有 <paramref name="type"/>.<paramref name="method"/>：一般方法直接比對；async 方法的框是編譯器產生的狀態機
    /// <c>&lt;Method&gt;d__N.MoveNext</c>，巢狀在宣告它的型別裡。
    /// </summary>
    private static bool OnStack(StackFrame[] frames, Type type, string method)
        => frames.Any(frame => frame.GetMethod() is { } m
            && ((m.DeclaringType == type && m.Name == method)
                || (m.Name == "MoveNext"
                    && m.DeclaringType is { } stateMachine
                    && stateMachine.DeclaringType == type
                    && stateMachine.Name.StartsWith("<" + method + ">", StringComparison.Ordinal))));
}

/// <summary>收集 Error 以上的日誌，用來斷言「流程沒有走到例外處理」。</summary>
internal sealed class ErrorLogCollector : ILoggerProvider
{
    private readonly List<string> entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (entries)
            {
                return entries.ToList();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Collector(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Collector(ErrorLogCollector owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                lock (owner.entries)
                {
                    owner.entries.Add($"{category}: {formatter(state, exception)}");
                }
            }
        }
    }
}

/// <summary>記錄送出的通知；可以讓下一則通知在「記下之後」卡住，模擬 commit＋通知之後、checkpoint 之前當機。</summary>
internal sealed class GatedNotificationSender : INotificationSender
{
    private readonly object sync = new();
    private Func<Task>? afterSend;

    public List<NotificationRequest> Requests { get; } = [];

    /// <summary>下一則通知記下之後先 await <paramref name="wait"/>（只觸發一次）。</summary>
    public void ArmAfterSend(Func<Task> wait) => Volatile.Write(ref afterSend, wait);

    public async Task<NotificationSendResult> SendAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            Requests.Add(request);
        }

        if (Interlocked.Exchange(ref afterSend, null) is { } wait)
        {
            await wait();
        }

        return new NotificationSendResult(1, 0);
    }
}

/// <summary>假的 AI 節點：行為可換、呼叫次數可查（跨 scope 共用）。</summary>
internal sealed class PreReviewFakes
{
    public int PhotoCalls;
    public int HistoryCalls;
    public int SummaryCalls;

    public Func<PreReviewCase, CancellationToken, Task<PreReviewFinding>> History { get; set; }
        = (_, _) => Task.FromResult(new PreReviewFinding(PreReviewNode.HistoryReview, FindingStatus.Ok, "沒有過往申請", "{}"));

    public Func<PreReviewCase, IReadOnlyList<PreReviewFinding>, CancellationToken, Task<PreReviewSummary>> Summarize { get; set; }
        = (_, _, _) => Task.FromResult(new PreReviewSummary(
            PreReviewRecommendations.Approve, "建議核准換貨", ["簽收 2 天、未穿過"], ["確認目標尺碼庫存"]));
}

internal sealed class FakePhotoReviewer(PreReviewFakes fakes) : IPhotoReviewer
{
    public Task<PreReviewFinding> ReviewAsync(PreReviewCase reviewCase, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref fakes.PhotoCalls);
        return Task.FromResult(new PreReviewFinding(PreReviewNode.PhotoReview, FindingStatus.Ok, "照片與描述一致", "{}"));
    }
}

internal sealed class FakeHistoryReviewer(PreReviewFakes fakes) : IHistoryReviewer
{
    public Task<PreReviewFinding> ReviewAsync(PreReviewCase reviewCase, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref fakes.HistoryCalls);
        return fakes.History(reviewCase, cancellationToken);
    }
}

internal sealed class FakeSummarizer(PreReviewFakes fakes) : IPreReviewSummarizer
{
    public Task<PreReviewSummary> SummarizeAsync(
        PreReviewCase reviewCase, IReadOnlyList<PreReviewFinding> findings, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref fakes.SummaryCalls);
        return fakes.Summarize(reviewCase, findings, cancellationToken);
    }
}
