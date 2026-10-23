using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;

namespace CustomerHelpAgent.Business.Startup;

/// <summary>
/// 升級時匯入舊設定 <c>AiSettings:SystemPrompt</c>（0.9.108 起改在「AI 提示詞」頁管理，設定鍵已移除）：
/// 設定有值、且日誌分析還沒有任何版本時，建立第 1 版並設為作用中，並記警告提醒移除該鍵。
/// 已有任何版本就不做事，所以只會匯入一次，之後在畫面上的修改不會被設定檔蓋回去。
/// </summary>
public sealed class LegacySystemPromptSeeder : IDatabaseSeeder
{
    public const string LegacyKey = "AiSettings:SystemPrompt";
    public const string ImportNote = "從設定檔 AiSettings:SystemPrompt 匯入";

    private readonly BackendDBContext dbContext;
    private readonly IConfiguration configuration;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<LegacySystemPromptSeeder> logger;

    public LegacySystemPromptSeeder(
        BackendDBContext dbContext,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<LegacySystemPromptSeeder> logger)
    {
        this.dbContext = dbContext;
        this.configuration = configuration;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public int Order => 40;

    public string Name => "匯入舊的 AI 系統提示詞設定";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var legacy = configuration[LegacyKey];
        if (string.IsNullOrWhiteSpace(legacy))
        {
            return;
        }

        if (await dbContext.PromptTemplate.AnyAsync(x => x.TemplateKey == PromptTemplateKeys.LogAnalysis, cancellationToken))
        {
            logger.LogWarning("Configuration key {LegacyKey} is no longer used; prompts are managed on the AI prompt page. Remove the key.", LegacyKey);
            return;
        }

        dbContext.PromptTemplate.Add(new PromptTemplate
        {
            TemplateKey = PromptTemplateKeys.LogAnalysis,
            Version = 1,
            Content = legacy.Trim(),
            IsActive = true,
            Note = ImportNote,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Imported {LegacyKey} as version 1 of the log analysis prompt. Remove the key from configuration.", LegacyKey);
    }
}
