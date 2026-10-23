using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Demo;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// 展示控制狀態（1.0.5 起，Singleton）：故障模擬、即時／回放模式、回放用的錄製、幕後面板預設。
/// 狀態存在 SQLite 單列（<see cref="DemoControlState"/>）並快取在記憶體；讀取不碰資料庫，寫入先存檔成功才換掉快取，再發出 <see cref="Changed"/>。
/// 啟動時由 <c>Program.cs</c> 在資料庫初始化之後呼叫 <see cref="LoadAsync"/>。
///
/// <list type="bullet">
/// <item><see cref="IsEnabled"/>（<c>DemoSettings:Enabled</c>）為 false 時一律視為無故障、即時模式，寫入一律拒絕（<see cref="InvalidOperationException"/>）。
/// Production 開啟示範模式會被 <c>StartupSafetyValidator</c> 擋下啟動。</item>
/// <item>⚠️ 本服務不寫稽核：它是 Singleton，拿不到登入者（<c>CurrentUserService</c> 是 Scoped）。
/// 呼叫端（展示控制台）改完後自己寫 <c>AuditActions.Demo.FaultChanged</c>／<c>ModeChanged</c>。</item>
/// </list>
/// </summary>
public sealed class DemoControlService
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly DemoSettings settings;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<DemoControlService> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile Snapshot state = Snapshot.Default;

    public DemoControlService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IOptions<DemoSettings> settings,
        TimeProvider timeProvider,
        ILogger<DemoControlService> logger)
    {
        this.contextFactory = contextFactory;
        this.settings = settings.Value;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>狀態改變（存檔成功）後觸發；訂閱者（Blazor 元件）要自行 <c>InvokeAsync</c> 回到自己的同步內容。</summary>
    public event Action? Changed;

    public bool IsEnabled => settings.Enabled;

    public IReadOnlySet<DemoFault> ActiveFaults => IsEnabled ? state.Faults : FrozenSet<DemoFault>.Empty;

    public DemoMode Mode => IsEnabled ? state.Mode : DemoMode.Live;

    public int? ActiveRecordingId => IsEnabled ? state.RecordingId : null;

    /// <summary>顧客頁幕後面板的預設開關；示範模式關閉時維持預設開。</summary>
    public bool BackstageDefaultOn => !IsEnabled || state.BackstageDefaultOn;

    /// <summary>
    /// <see cref="DemoFault.OrderServiceTimeout"/> 讓訂單讀取等待多久才拋逾時。預設 30 秒，刻意長於 Agent 工具逾時（預設 15 秒），
    /// 讓工具先以 <c>timeout</c> 回報；傳統頁面則在這段時間後看到錯誤，不會無限等待。測試可縮短。
    /// </summary>
    public TimeSpan FaultDelay { get; internal set; } = TimeSpan.FromSeconds(30);

    public bool IsFaultActive(DemoFault fault) => IsEnabled && state.Faults.Contains(fault);

    /// <summary>從資料庫載入狀態（沒有資料列就維持預設）。示範模式關閉時不讀資料庫。</summary>
    public async Task LoadAsync()
    {
        if (!IsEnabled)
        {
            return;
        }

        await gate.WaitAsync();
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var row = await context.DemoControlState.AsNoTracking().FirstOrDefaultAsync(x => x.Id == DemoControlState.SingletonId);
            var mode = row?.Mode ?? DemoMode.Live;
            if (row is not null && mode == DemoMode.Replay && row.ActiveRecordingId is null)
            {
                // 回放必須有錄製（SetModeAsync 也擋）；資料列不一致時回到即時，畫面才不會顯示「回放中」卻照常呼叫模型。
                logger.LogWarning("Demo control state is in replay mode without a recording; falling back to live mode.");
                mode = DemoMode.Live;
            }

            state = row is null
                ? Snapshot.Default
                : new Snapshot(ParseFaults(row.FaultsJson), mode, mode == DemoMode.Replay ? row.ActiveRecordingId : null, row.BackstageDefaultOn);
            logger.LogInformation(
                "Demo control state loaded. FaultCount={FaultCount}, Mode={Mode}, ActiveRecordingId={ActiveRecordingId}",
                state.Faults.Count, state.Mode, state.RecordingId);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task SetFaultAsync(DemoFault fault, bool on)
        => UpdateAsync(current =>
        {
            var faults = current.Faults.ToHashSet();
            if (on)
            {
                faults.Add(fault);
            }
            else
            {
                faults.Remove(fault);
            }

            return current with { Faults = faults.ToFrozenSet() };
        });

    public Task ClearFaultsAsync() => UpdateAsync(current => current with { Faults = FrozenSet<DemoFault>.Empty });

    /// <summary>切換模式。回放必須指定錄製；即時模式不保留錄製。</summary>
    public Task SetModeAsync(DemoMode mode, int? recordingId)
    {
        if (mode == DemoMode.Replay && recordingId is null)
        {
            throw new ArgumentException("回放模式必須指定錄製。", nameof(recordingId));
        }

        return UpdateAsync(current => current with { Mode = mode, RecordingId = mode == DemoMode.Replay ? recordingId : null });
    }

    public Task SetBackstageDefaultAsync(bool on) => UpdateAsync(current => current with { BackstageDefaultOn = on });

    /// <summary>服務注入故障時取時間（例如物流異常事件），與本服務用同一個時鐘。</summary>
    internal DateTime Now() => timeProvider.GetLocalNow().DateTime;

    private async Task UpdateAsync(Func<Snapshot, Snapshot> change)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("示範模式未開啟（DemoSettings:Enabled），無法變更展示狀態。");
        }

        await gate.WaitAsync();
        try
        {
            var next = change(state);
            await using var context = await contextFactory.CreateDbContextAsync();
            var row = await context.DemoControlState.FirstOrDefaultAsync(x => x.Id == DemoControlState.SingletonId);
            if (row is null)
            {
                row = new DemoControlState { Id = DemoControlState.SingletonId };
                context.DemoControlState.Add(row);
            }

            row.FaultsJson = JsonSerializer.Serialize(next.Faults.Select(x => x.ToString()).Order(StringComparer.Ordinal), JsonOptions);
            row.Mode = next.Mode;
            row.ActiveRecordingId = next.RecordingId;
            row.BackstageDefaultOn = next.BackstageDefaultOn;
            row.UpdatedAt = Now();
            await context.SaveChangesAsync();

            state = next;
            logger.LogInformation(
                "Demo control state changed. Faults={Faults}, Mode={Mode}, ActiveRecordingId={ActiveRecordingId}, BackstageDefaultOn={BackstageDefaultOn}",
                row.FaultsJson, next.Mode, next.RecordingId, next.BackstageDefaultOn);
        }
        finally
        {
            gate.Release();
        }

        RaiseChanged();
    }

    /// <summary>逐一通知訂閱者；單一訂閱者失敗只記警告，不影響其他訂閱者，也不讓已存檔的變更看起來像失敗。</summary>
    private void RaiseChanged()
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Demo control change subscriber failed.");
            }
        }
    }

    /// <summary>資料庫裡不認得的故障名稱（舊版本留下）略過並記警告。</summary>
    private FrozenSet<DemoFault> ParseFaults(string json)
    {
        string[] names;
        try
        {
            names = JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Demo control faults could not be parsed; treating as no faults.");
            return FrozenSet<DemoFault>.Empty;
        }

        var faults = new HashSet<DemoFault>();
        foreach (var name in names)
        {
            if (Enum.TryParse<DemoFault>(name, ignoreCase: false, out var fault) && Enum.IsDefined(fault))
            {
                faults.Add(fault);
            }
            else
            {
                logger.LogWarning("Unknown demo fault ignored. Fault={Fault}", name);
            }
        }

        return faults.ToFrozenSet();
    }

    private sealed record Snapshot(FrozenSet<DemoFault> Faults, DemoMode Mode, int? RecordingId, bool BackstageDefaultOn)
    {
        public static readonly Snapshot Default = new(FrozenSet<DemoFault>.Empty, DemoMode.Live, null, true);
    }
}
