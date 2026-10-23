using CustomerHelpAgent.Business.Services.DataAccess;

namespace CustomerHelpAgent.Web.Configuration.Parameters;

/// <summary>
/// 把資料庫裡的系統參數覆寫套用到設定（0.9.98 起）。啟動時（資料庫初始化之後）、每次存檔後、以及每分鐘
/// （<see cref="SystemParameterRefreshWorker"/>，讓多個行程或多台主機同步）各執行一次。
///
/// 套用規則：
/// <list type="bullet">
/// <item>⚠️ 不在 <see cref="SystemParameterCatalog"/> 的鍵<b>一律不套用</b>（安全邊界：資料庫裡就算有 <c>JwtSettings:SigningKey</c>
/// 也進不了設定），列為「孤兒」讓管理員移除。</item>
/// <item>解析失敗、或與設定檔合起來驗證不過的覆寫（例如升級後範圍變窄）略過並標「未套用」，不讓啟動失敗。
/// 同一區段依目錄順序逐一加入，每加一個就驗證一次整個區段。</item>
/// <item>設定檔本身驗證不過（執行中把 appsettings 改壞）的區段，維持目前套用的覆寫不動。</item>
/// <item>內容沒變就不觸發 reload。</item>
/// </list>
/// </summary>
public sealed class SystemParameterRuntime
{
    private readonly SystemParameterService parameterService;
    private readonly SystemParameterValidator validator;
    private readonly SystemParameterConfigurationProvider provider;
    private readonly ILogger<SystemParameterRuntime> logger;
    private readonly SemaphoreSlim gate = new(1, 1);

    public SystemParameterRuntime(
        SystemParameterService parameterService,
        SystemParameterValidator validator,
        SystemParameterConfigurationProvider provider,
        ILogger<SystemParameterRuntime> logger)
    {
        this.parameterService = parameterService;
        this.validator = validator;
        this.provider = provider;
        this.logger = logger;
    }

    /// <summary>資料庫裡有、但沒有套用的覆寫：鍵 → 原因。</summary>
    public IReadOnlyDictionary<string, string> Rejected { get; private set; } = new Dictionary<string, string>();

    /// <summary>資料庫裡有、但不在目錄裡的鍵（舊版本留下或被人直接寫入）。</summary>
    public IReadOnlyList<string> Orphans { get; private set; } = [];

    /// <summary>設定檔本身驗證不過的區段：區段 → 錯誤。這些區段暫時不能存檔。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> BaseInvalid { get; private set; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>啟動時載入。讀不到資料庫就讓啟動失敗（與資料庫初始化一致）。</summary>
    public Task LoadAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var rows = await parameterService.GetAllAsync(cancellationToken);
            var current = provider.Snapshot;
            var applied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var rejected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var baseInvalid = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var orphans = rows.Where(x => SystemParameterCatalog.Find(x.ParameterKey) is null).Select(x => x.ParameterKey).ToList();
            var rowsByKey = rows.Where(x => SystemParameterCatalog.Find(x.ParameterKey) is not null)
                .ToDictionary(x => x.ParameterKey, x => x.Value, StringComparer.OrdinalIgnoreCase);

            foreach (var section in SystemParameterCatalog.Sections)
            {
                // 每個區段都檢查設定檔本身（頁面要能提示「設定檔有誤」，即使這一區還沒有任何覆寫）。
                var baseErrors = validator.Validate(section, new Dictionary<string, string>());
                if (baseErrors.Count > 0)
                {
                    baseInvalid[section] = baseErrors;
                }

                var definitions = SystemParameterCatalog.All
                    .Where(x => string.Equals(x.SectionName, section, StringComparison.OrdinalIgnoreCase) && rowsByKey.ContainsKey(x.Key))
                    .ToList();
                if (definitions.Count == 0)
                {
                    continue;
                }

                if (baseErrors.Count > 0)
                {
                    foreach (var definition in definitions.Where(x => current.ContainsKey(x.Key)))
                    {
                        applied[definition.Key] = current[definition.Key];
                    }

                    continue;
                }

                var accepted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var definition in definitions)
                {
                    if (!SystemParameterValueCodec.TryNormalize(definition, rowsByKey[definition.Key], out var value, out var parseError))
                    {
                        rejected[definition.Key] = parseError;
                        continue;
                    }

                    var trial = new Dictionary<string, string>(accepted, StringComparer.OrdinalIgnoreCase) { [definition.Key] = value };
                    var errors = validator.Validate(section, trial);
                    if (errors.Count > 0)
                    {
                        rejected[definition.Key] = string.Join(" ", errors);
                        continue;
                    }

                    accepted[definition.Key] = value;
                }

                foreach (var (key, value) in accepted)
                {
                    applied[key] = value;
                }
            }

            LogNewProblems(rejected, orphans);
            Rejected = rejected;
            Orphans = orphans;
            BaseInvalid = baseInvalid;

            if (!SameContent(current, applied))
            {
                provider.Apply(applied);
                logger.LogInformation("System parameter overrides applied. Count={Count}", applied.Count);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>同一個問題只在第一次發現時記錯誤（每分鐘刷新，不要洗版）。不附例外物件，不會重複算進系統例外紀錄。</summary>
    private void LogNewProblems(Dictionary<string, string> rejected, List<string> orphans)
    {
        foreach (var (key, reason) in rejected)
        {
            if (!Rejected.TryGetValue(key, out var previous) || previous != reason)
            {
                logger.LogError("System parameter override was not applied. ParameterKey={ParameterKey}, Reason={Reason}", key, reason);
            }
        }

        foreach (var key in orphans.Where(x => !Orphans.Contains(x, StringComparer.OrdinalIgnoreCase)))
        {
            logger.LogWarning("Unknown system parameter key ignored. ParameterKey={ParameterKey}", key);
        }
    }

    private static bool SameContent(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count && left.All(x => right.TryGetValue(x.Key, out var value) && value == x.Value);
}
