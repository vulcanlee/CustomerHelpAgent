using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.AdapterModel;

namespace CustomerHelpAgent.Web.Configuration.Parameters;

public enum SystemParameterSource
{
    /// <summary>沒有覆寫，用設定檔（appsettings／環境變數）的值。</summary>
    File,

    /// <summary>用「系統參數」頁設定的值。</summary>
    Override,

    /// <summary>資料庫裡有覆寫，但不合法而沒有套用，目前用設定檔的值。</summary>
    NotApplied,
}

/// <summary>管理頁的一列。</summary>
public sealed class SystemParameterOverviewItem
{
    public required SystemParameterDefinition Definition { get; init; }

    /// <summary>目前生效的值（設定讀到的）。</summary>
    public string? EffectiveValue { get; init; }

    /// <summary>設定檔這一層的值。</summary>
    public string? BaseValue { get; init; }

    public SystemParameterAdapterModel? Override { get; init; }

    public SystemParameterSource Source { get; init; }

    /// <summary>未套用的原因。</summary>
    public string? Problem { get; init; }
}

public sealed class SystemParameterOverview
{
    public required IReadOnlyList<SystemParameterOverviewItem> Items { get; init; }

    /// <summary>資料庫裡不在目錄的鍵（不會套用），讓管理員移除。</summary>
    public required IReadOnlyList<SystemParameterAdapterModel> Orphans { get; init; }

    /// <summary>設定檔本身有誤的區段與錯誤。</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> BaseInvalid { get; init; }
}

/// <summary>存檔或還原的結果；成功時附稽核用的 Detail。</summary>
public sealed record SystemParameterChangeResult(bool Success, string Message, string? AuditDetail = null, bool IsReset = false);

/// <summary>
/// 「系統參數」頁使用的門面（0.9.98 起）：列出參數、存檔、還原為設定檔值。
/// 每次修改先驗證（與設定檔合起來的整個區段），通過才寫資料庫，寫完立刻重新套用 —— 存檔後不需重啟就生效。
/// </summary>
public sealed class SystemParameterManager
{
    internal const int MaxAuditValueLength = 200;

    private readonly SystemParameterService parameterService;
    private readonly SystemParameterRuntime runtime;
    private readonly SystemParameterValidator validator;
    private readonly SystemParameterConfigurationProvider provider;
    private readonly IConfiguration configuration;
    private readonly ILogger<SystemParameterManager> logger;

    public SystemParameterManager(
        SystemParameterService parameterService,
        SystemParameterRuntime runtime,
        SystemParameterValidator validator,
        SystemParameterConfigurationProvider provider,
        IConfiguration configuration,
        ILogger<SystemParameterManager> logger)
    {
        this.parameterService = parameterService;
        this.runtime = runtime;
        this.validator = validator;
        this.provider = provider;
        this.configuration = configuration;
        this.logger = logger;
    }

    public async Task<SystemParameterOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var rows = (await parameterService.GetAllAsync(cancellationToken))
            .ToDictionary(x => x.ParameterKey, StringComparer.OrdinalIgnoreCase);
        var applied = provider.Snapshot;

        var items = SystemParameterCatalog.All.Select(definition =>
        {
            rows.TryGetValue(definition.Key, out var row);
            string? problem = null;
            var source = SystemParameterSource.File;
            if (row is not null)
            {
                if (applied.ContainsKey(definition.Key))
                {
                    source = SystemParameterSource.Override;
                }
                else
                {
                    source = SystemParameterSource.NotApplied;
                    problem = runtime.Rejected.GetValueOrDefault(definition.Key)
                        ?? (runtime.BaseInvalid.ContainsKey(definition.SectionName) ? "設定檔本身目前有誤。" : "尚未套用，請按重新整理。");
                }
            }

            return new SystemParameterOverviewItem
            {
                Definition = definition,
                EffectiveValue = configuration[definition.Key],
                BaseValue = validator.GetBaseValue(definition.Key),
                Override = row,
                Source = source,
                Problem = problem,
            };
        }).ToList();

        return new SystemParameterOverview
        {
            Items = items,
            Orphans = rows.Values.Where(x => SystemParameterCatalog.Find(x.ParameterKey) is null).ToList(),
            BaseInvalid = runtime.BaseInvalid,
        };
    }

    /// <summary>
    /// 存檔。<paramref name="expectedStamp"/> 是開窗時的版本號（開窗時沒有覆寫就傳 null）。
    /// 新值等於設定檔的值時改為還原（刪除覆寫），不留一筆「剛好等於設定檔」的覆寫。
    /// </summary>
    public async Task<SystemParameterChangeResult> SaveAsync(string key, string? rawValue, string? expectedStamp, string? account, CancellationToken cancellationToken = default)
    {
        var definition = SystemParameterCatalog.Find(key);
        if (definition is null)
        {
            return new(false, "這個參數不能在這裡修改。");
        }

        if (!SystemParameterValueCodec.TryNormalize(definition, rawValue, out var value, out var parseError))
        {
            return new(false, parseError);
        }

        // 直接驗證設定檔本身，不靠上一次刷新的結果：別人可能剛改壞設定檔，不要把那些錯誤算到管理員的輸入上。
        var baseErrors = validator.Validate(definition.SectionName, new Dictionary<string, string>());
        if (baseErrors.Count > 0)
        {
            return new(false, $"設定檔本身目前有誤，修正之前不能修改這一區的參數：{string.Join(" ", baseErrors)}");
        }

        var effective = SystemParameterValueCodec.Canonical(definition, configuration[definition.Key]);
        var baseValue = SystemParameterValueCodec.Canonical(definition, validator.GetBaseValue(definition.Key));
        var oldSource = provider.Snapshot.ContainsKey(definition.Key) ? "系統參數" : "設定檔";

        if (value == baseValue)
        {
            if (expectedStamp is not null)
            {
                return await ResetAsync(definition.Key, expectedStamp, cancellationToken);
            }

            return new(false, "值沒有變更（與設定檔的值相同）。");
        }

        if (value == effective && oldSource == "系統參數")
        {
            return new(false, "值沒有變更。");
        }

        var candidate = SectionOverrides(definition.SectionName);
        candidate[definition.Key] = value;
        var errors = validator.Validate(definition.SectionName, candidate);
        if (errors.Count > 0)
        {
            return new(false, string.Join(" ", errors));
        }

        var saved = await parameterService.UpsertAsync(definition.Key, value, expectedStamp, account, cancellationToken);
        if (!saved.Success)
        {
            return new(false, saved.Message);
        }

        await runtime.RefreshAsync(cancellationToken);
        logger.LogInformation("System parameter saved. ParameterKey={ParameterKey}", definition.Key);
        return new(true, $"已更新「{definition.Label}」為 {SystemParameterValueCodec.Display(definition, value)}。",
            BuildAuditDetail(definition.Key, effective, oldSource, value, "系統參數"));
    }

    /// <summary>還原為設定檔的值（刪除覆寫）。不在目錄的孤兒鍵也可以用它移除。</summary>
    public async Task<SystemParameterChangeResult> ResetAsync(string key, string expectedStamp, CancellationToken cancellationToken = default)
    {
        var definition = SystemParameterCatalog.Find(key);
        string? oldValue = null;
        string? newValue = null;
        if (definition is not null)
        {
            var candidate = SectionOverrides(definition.SectionName);
            candidate.Remove(definition.Key);
            var errors = validator.Validate(definition.SectionName, candidate);
            if (errors.Count > 0)
            {
                return new(false, string.Join(" ", errors));
            }

            oldValue = SystemParameterValueCodec.Canonical(definition, configuration[definition.Key]);
            newValue = SystemParameterValueCodec.Canonical(definition, validator.GetBaseValue(definition.Key));
        }

        var deleted = await parameterService.DeleteAsync(key, expectedStamp, cancellationToken);
        if (!deleted.Success)
        {
            return new(false, deleted.Message);
        }

        await runtime.RefreshAsync(cancellationToken);
        logger.LogInformation("System parameter reset to the configuration file value. ParameterKey={ParameterKey}", key);
        var message = definition is null
            ? $"已移除「{key}」。"
            : $"已將「{definition.Label}」還原為設定檔的值 {SystemParameterValueCodec.Display(definition, newValue)}。";
        return new(true, message, BuildAuditDetail(key, oldValue, "系統參數", newValue, "設定檔"), IsReset: true);
    }

    /// <summary><c>key=…; old=365(設定檔); new=30(系統參數)</c>，值過長截斷。</summary>
    internal static string BuildAuditDetail(string key, string? oldValue, string oldSource, string? newValue, string newSource)
        => $"key={key}; old={Truncate(oldValue)}({oldSource}); new={Truncate(newValue)}({newSource})";

    private static string Truncate(string? value)
    {
        var text = value ?? "（無）";
        return text.Length <= MaxAuditValueLength ? text : text[..MaxAuditValueLength] + "…";
    }

    private Dictionary<string, string> SectionOverrides(string sectionName)
        => provider.Snapshot
            .Where(x => x.Key.StartsWith(sectionName + ":", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
}
