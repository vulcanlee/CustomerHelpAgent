using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Models.Agents;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Demo;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// 售後 Agent 會話錄製的持久化與查詢（1.0.5 起）。
///
/// <list type="bullet">
/// <item>寫入（<see cref="AppendTurnAsync"/>、<see cref="AppendCallAsync"/>）是旁路資訊：任何失敗都只記 Warning、不拋出，不影響顧客的對話。
/// 是否要錄製由呼叫端決定（Runner：示範模式開啟且為即時模式）。</item>
/// <item>併發假設與 <see cref="AgentTraceService"/> 相同：同一段會話只有單一寫入者（Runner 依序呼叫），Sequence 以「max + 1」計算。
/// 動作與呼叫各自編號（各由 1 起）。</item>
/// <item>費用（<see cref="AgentRecording.TotalCostTwd"/>）：每記一筆呼叫，就以 AI 對話紀錄的 <c>ConversationId</c>（＝SessionKey）
/// 對到 Token 用量的 <c>CostTwd</c> 在記憶體加總後存下——存起來是為了 AI 對話紀錄依保留天數過期後仍看得到；
/// AI 對話紀錄關閉（<c>AiCallLogSettings:Enabled=false</c>）時對不到，維持 null。</item>
/// <item>金劇本與刪除不寫稽核：比照 <c>DemoControlService</c>，由呼叫端（展示控制台）以登入者身分寫 <c>AuditActions.Demo.RecordingGolden</c>。</item>
/// </list>
/// </summary>
public class AgentRecordingService
{
    /// <summary>標題取第一句顧客訊息的前幾個字。</summary>
    public const int TitleLength = 20;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AgentRecordingService> logger;

    public AgentRecordingService(
        IDbContextFactory<BackendDBContext> contextFactory,
        TimeProvider timeProvider,
        ILogger<AgentRecordingService> logger)
    {
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    private DateTime Now => timeProvider.GetLocalNow().DateTime;

    /// <summary>記一個顧客動作；這段會話還沒有錄製就先建立（標題取第一句顧客訊息的前 <see cref="TitleLength"/> 字）。不拋例外。</summary>
    public async Task AppendTurnAsync(Guid sessionKey, int customerUserId, AgentRecordedTurnKind kind, string? customerText)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var now = Now;
            var turn = new AgentRecordedTurn { Kind = kind, CustomerText = customerText, CreatedAt = now };
            var recording = await context.AgentRecording.FirstOrDefaultAsync(x => x.SessionKey == sessionKey);
            if (recording is null)
            {
                turn.Sequence = 1;
                context.AgentRecording.Add(new AgentRecording
                {
                    SessionKey = sessionKey,
                    CustomerUserId = customerUserId,
                    Title = MakeTitle(customerText),
                    CreatedAt = now,
                    Turns = [turn],
                });
                await context.SaveChangesAsync();
                logger.LogInformation("Agent recording started. SessionKey={SessionKey}", sessionKey);
                return;
            }

            if (recording.Title.Length == 0)
            {
                // 第一個動作沒有文字（例如會話中途才開始錄製、先按了確認）：以之後第一句顧客訊息當標題。
                recording.Title = MakeTitle(customerText);
            }

            var last = await context.AgentRecordedTurn
                .Where(x => x.AgentRecordingId == recording.Id)
                .MaxAsync(x => (int?)x.Sequence) ?? 0;
            turn.AgentRecordingId = recording.Id;
            turn.Sequence = last + 1;
            context.AgentRecordedTurn.Add(turn);
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record an agent customer turn. SessionKey={SessionKey}, Kind={Kind}", sessionKey, kind);
        }
    }

    /// <summary>
    /// 記一次成功的模型呼叫，並更新模型名稱（第一次有值時）與費用合計。這段會話沒有錄製（建立失敗）就略過。不拋例外。
    /// <paramref name="stream"/>：呼叫來源（<see cref="AgentRecordingStreams"/>），Sequence 在同一條 stream 內各自編號 ——
    /// AI 初審的照片與歷史節點併行寫入同一段錄製，各寫各的 stream 才不會互搶「max + 1」。
    /// </summary>
    public async Task AppendCallAsync(
        Guid sessionKey,
        string requestDigest,
        string responseJson,
        string? usageJson,
        string? modelName,
        string stream = AgentRecordingStreams.Customer)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var recording = await context.AgentRecording.FirstOrDefaultAsync(x => x.SessionKey == sessionKey);
            if (recording is null)
            {
                logger.LogWarning("Agent model call not recorded because the session has no recording. SessionKey={SessionKey}", sessionKey);
                return;
            }

            var last = await context.AgentRecordedCall
                .Where(x => x.AgentRecordingId == recording.Id && x.Stream == stream)
                .MaxAsync(x => (int?)x.Sequence) ?? 0;
            context.AgentRecordedCall.Add(new AgentRecordedCall
            {
                AgentRecordingId = recording.Id,
                Stream = stream,
                Sequence = last + 1,
                RequestDigest = requestDigest,
                ResponseJson = responseJson,
                UsageJson = usageJson,
                CreatedAt = Now,
            });
            if (recording.ModelName.Length == 0 && !string.IsNullOrWhiteSpace(modelName))
            {
                recording.ModelName = modelName;
            }

            recording.TotalCostTwd = await SumCostAsync(context, sessionKey);
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record an agent model call. SessionKey={SessionKey}, Stream={Stream}", sessionKey, stream);
        }
    }

    public async Task<DataRequestResult<AgentRecordingSummary>> GetRecordingsAsync(DataRequest dataRequest)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        // Skip/Take 一定要搭配 OrderBy。
        var ordered = context.AgentRecording.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id);

        DataRequestResult<AgentRecordingSummary> result = new() { Count = await ordered.CountAsync() };

        var paged = ordered.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            paged = paged.Take(dataRequest.PageSize);
        }

        result.Result = await ToSummaries(context, paged).ToListAsync();
        return result;
    }

    /// <summary>一段錄製與它的顧客動作；找不到回 null。</summary>
    public async Task<AgentRecordingDetail?> GetAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var summary = await ToSummaries(context, context.AgentRecording.AsNoTracking().Where(x => x.Id == id)).FirstOrDefaultAsync();
        if (summary is null)
        {
            return null;
        }

        var turns = await context.AgentRecordedTurn.AsNoTracking()
            .Where(x => x.AgentRecordingId == id)
            .OrderBy(x => x.Sequence).ThenBy(x => x.Id)
            .Select(x => new AgentRecordedTurnView(x.Sequence, x.Kind, x.CustomerText, x.CreatedAt))
            .ToListAsync();
        return new AgentRecordingDetail(summary, turns);
    }

    /// <summary>錄製中某一條 stream（預設顧客端 Agent）的模型呼叫（依順序）；找不到錄製回空清單。</summary>
    public async Task<IReadOnlyList<AgentRecordedCallView>> GetCallsAsync(int id, string stream = AgentRecordingStreams.Customer)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.AgentRecordedCall.AsNoTracking()
            .Where(x => x.AgentRecordingId == id && x.Stream == stream)
            .OrderBy(x => x.Sequence).ThenBy(x => x.Id)
            .Select(x => new AgentRecordedCallView(x.Sequence, x.RequestDigest, x.ResponseJson, x.UsageJson, x.CreatedAt))
            .ToListAsync();
    }

    /// <summary>設為劇本的金劇本；同一劇本原本的金劇本降級（保留劇本代碼）。找不到錄製回 false。</summary>
    public async Task<bool> SetGoldenAsync(int id, string scenarioCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioCode);
        var code = scenarioCode.Trim();

        await using var context = await contextFactory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var recording = await context.AgentRecording.FirstOrDefaultAsync(x => x.Id == id);
        if (recording is null)
        {
            return false;
        }

        await context.AgentRecording
            .Where(x => x.IsGolden && x.ScenarioCode == code && x.Id != id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsGolden, false));
        recording.IsGolden = true;
        recording.ScenarioCode = code;
        await context.SaveChangesAsync();
        await transaction.CommitAsync();

        logger.LogInformation("Agent recording set as golden. RecordingId={RecordingId}, ScenarioCode={ScenarioCode}", id, code);
        return true;
    }

    /// <summary>劇本目前的金劇本；沒有回 null。</summary>
    public async Task<AgentRecordingSummary?> GetGoldenAsync(string scenarioCode)
    {
        var code = scenarioCode.Trim();
        await using var context = await contextFactory.CreateDbContextAsync();
        return await ToSummaries(context, context.AgentRecording.AsNoTracking().Where(x => x.IsGolden && x.ScenarioCode == code))
            .FirstOrDefaultAsync();
    }

    /// <summary>刪除錄製（連同動作與呼叫）。找不到回 false。</summary>
    public async Task<bool> DeleteAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.AgentRecordedTurn.Where(x => x.AgentRecordingId == id).ExecuteDeleteAsync();
        await context.AgentRecordedCall.Where(x => x.AgentRecordingId == id).ExecuteDeleteAsync();
        var deleted = await context.AgentRecording.Where(x => x.Id == id).ExecuteDeleteAsync();
        await transaction.CommitAsync();

        if (deleted > 0)
        {
            logger.LogInformation("Agent recording deleted. RecordingId={RecordingId}", id);
        }

        return deleted > 0;
    }

    /// <summary>第一行去頭尾空白後取前 <see cref="TitleLength"/> 字（不切開代理字組）。</summary>
    private static string MakeTitle(string? customerText)
    {
        var line = (customerText ?? string.Empty).Split('\n')[0].Trim();
        if (line.Length <= TitleLength)
        {
            return line;
        }

        var length = char.IsHighSurrogate(line[TitleLength - 1]) ? TitleLength - 1 : TitleLength;
        return line[..length];
    }

    /// <summary>這段會話所有已定價呼叫的台幣費用合計；費用是 double（REAL），在記憶體加總後轉 decimal。</summary>
    private static async Task<decimal?> SumCostAsync(BackendDBContext context, Guid sessionKey)
    {
        var costs = await (
            from usage in context.TokenUsageLog.AsNoTracking()
            join call in context.AiCallLog.AsNoTracking() on usage.CallId equals (Guid?)call.CallId
            where call.ConversationId == sessionKey && usage.CostTwd != null
            select usage.CostTwd!.Value).ToListAsync();
        return costs.Count == 0 ? null : (decimal)costs.Sum();
    }

    private static IQueryable<AgentRecordingSummary> ToSummaries(BackendDBContext context, IQueryable<AgentRecording> source)
    {
        // 顧客姓名不經導覽屬性：MyUser 有軟刪除過濾器，導覽會讓已刪除顧客的錄製消失（速查表 §7）。
        var users = context.MyUser.IgnoreQueryFilters([ISoftDeletable.FilterName]);
        return source.Select(x => new AgentRecordingSummary(
            x.Id,
            x.SessionKey,
            x.Title,
            users.Where(u => u.Id == x.CustomerUserId).Select(u => u.Name).FirstOrDefault() ?? string.Empty,
            x.IsGolden,
            x.ScenarioCode,
            x.ModelName,
            x.TotalCostTwd,
            x.Turns.Count,
            x.Calls.Count,
            x.CreatedAt));
    }
}
