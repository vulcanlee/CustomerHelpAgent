using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Factories;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>「AI 提示詞」頁顯示的一個版本（時間為伺服器本地時間）。</summary>
public sealed record PromptTemplateVersion(int Version, string Content, bool IsActive, string? Note, string? CreatedByAccount, DateTime CreatedAt);

/// <summary>
/// AI 提示詞的版本管理（0.9.108 起）。只存可修改的分析指示：沒有作用中的版本時，呼叫端（Web 的
/// <c>AiSystemPromptProvider</c>）改用程式內建的預設；防注入與 Markdown 規則由呼叫端固定接在最後。
/// 每次儲存都新增一個版本並設為作用中，舊版本全部保留，可隨時切回。
/// </summary>
public class PromptTemplateService
{
    internal const int MaxContentLength = 8000;
    internal const int MaxNoteLength = 200;
    public const string ConflictMessage = "這個提示詞在你編輯期間已被其他人修改。請關閉視窗、重新整理後再編輯。";
    private const int SqliteConstraintErrorCode = 19;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<PromptTemplateService> logger;

    public PromptTemplateService(IDbContextFactory<BackendDBContext> contextFactory, TimeProvider timeProvider, ILogger<PromptTemplateService> logger)
    {
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>作用中版本的內容；沒有作用中的版本時為 null（呼叫端改用內建預設）。</summary>
    public async Task<string?> GetActiveContentAsync(string templateKey, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PromptTemplate.AsNoTracking()
            .Where(x => x.TemplateKey == templateKey && x.IsActive)
            .Select(x => x.Content)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>所有版本，新到舊。</summary>
    public async Task<List<PromptTemplateVersion>> GetHistoryAsync(string templateKey)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var rows = await context.PromptTemplate.AsNoTracking()
            .Where(x => x.TemplateKey == templateKey)
            .OrderByDescending(x => x.Version)
            .ToListAsync();
        return rows.Select(x => new PromptTemplateVersion(x.Version, x.Content, x.IsActive, x.Note, x.CreatedByAccount, ToLocal(x.CreatedAtUtc))).ToList();
    }

    /// <summary>
    /// 存成新版本並設為作用中。<paramref name="expectedActiveVersion"/> 是開啟編輯窗時的作用中版本（內建預設為 null）；
    /// 與目前不同代表別人在這段期間改過，不儲存。
    /// </summary>
    public async Task<(VerifyRecordResult Result, int Version)> SaveNewVersionAsync(
        string templateKey, string content, string? note, int? expectedActiveVersion, string? account)
    {
        var error = Validate(templateKey, content, note);
        if (error is not null)
        {
            return (VerifyRecordResultFactory.Build(false, error), 0);
        }

        await using var context = await contextFactory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var active = await ActiveVersionAsync(context, templateKey);
        if (active != expectedActiveVersion)
        {
            return (VerifyRecordResultFactory.Build(false, ConflictMessage), 0);
        }

        var version = (await context.PromptTemplate.Where(x => x.TemplateKey == templateKey).MaxAsync(x => (int?)x.Version) ?? 0) + 1;
        await context.PromptTemplate.Where(x => x.TemplateKey == templateKey && x.IsActive)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.IsActive, false));
        context.PromptTemplate.Add(new PromptTemplate
        {
            TemplateKey = templateKey,
            Version = version,
            Content = content.Trim(),
            IsActive = true,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedByAccount = account,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        });
        try
        {
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraintErrorCode })
        {
            // 兩個人同時儲存：版本號或「唯一作用中」的唯一索引擋下其中一個。
            logger.LogWarning("Prompt template version was not saved because of a concurrent save. TemplateKey={TemplateKey}", templateKey);
            return (VerifyRecordResultFactory.Build(false, ConflictMessage), 0);
        }

        logger.LogInformation("Prompt template saved. TemplateKey={TemplateKey}, Version={Version}", templateKey, version);
        return (VerifyRecordResultFactory.Build(true), version);
    }

    /// <summary>切換作用中的版本；<paramref name="version"/> 為 null 代表改用內建預設（所有版本都不作用）。</summary>
    public async Task<VerifyRecordResult> ActivateAsync(string templateKey, int? version)
    {
        if (!PromptTemplateKeys.All.Contains(templateKey))
        {
            return VerifyRecordResultFactory.Build(false, "提示詞種類不正確。");
        }

        await using var context = await contextFactory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        if (version is { } v && !await context.PromptTemplate.AnyAsync(x => x.TemplateKey == templateKey && x.Version == v))
        {
            return VerifyRecordResultFactory.Build(false, $"找不到第 {v} 版。");
        }

        await context.PromptTemplate.Where(x => x.TemplateKey == templateKey && x.IsActive)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.IsActive, false));
        if (version is { } target)
        {
            await context.PromptTemplate.Where(x => x.TemplateKey == templateKey && x.Version == target)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.IsActive, true));
        }

        await transaction.CommitAsync();
        logger.LogInformation("Prompt template activated. TemplateKey={TemplateKey}, Version={Version}", templateKey, version);
        return VerifyRecordResultFactory.Build(true);
    }

    private static async Task<int?> ActiveVersionAsync(BackendDBContext context, string templateKey)
        => await context.PromptTemplate.Where(x => x.TemplateKey == templateKey && x.IsActive).Select(x => (int?)x.Version).FirstOrDefaultAsync();

    private static string? Validate(string templateKey, string content, string? note)
    {
        if (!PromptTemplateKeys.All.Contains(templateKey))
        {
            return "提示詞種類不正確。";
        }

        if (string.IsNullOrWhiteSpace(content) || content.Trim().Length > MaxContentLength)
        {
            return $"提示詞不可留空，最多 {MaxContentLength} 個字。";
        }

        if (note is not null && note.Trim().Length > MaxNoteLength)
        {
            return $"備註最多 {MaxNoteLength} 個字。";
        }

        return null;
    }

    private DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeProvider.LocalTimeZone);
}
