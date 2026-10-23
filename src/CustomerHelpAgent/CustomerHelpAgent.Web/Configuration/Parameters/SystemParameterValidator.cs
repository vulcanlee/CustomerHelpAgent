namespace CustomerHelpAgent.Web.Configuration.Parameters;

/// <summary>
/// 驗證一組系統參數覆寫（0.9.98 起）：把「設定檔的值＋候選覆寫」組成記憶體裡的設定區段，交給真的 options 管線驗證。
///
/// ⚠️ 存檔前一定要先驗證：<c>IOptionsMonitor.CurrentValue</c> 遇到不合法的設定會丟例外，所有讀它的地方都會壞。
/// ⚠️ 不可拿現有的 provider 建新的 <c>ConfigurationRoot</c>：建構子會呼叫每個 provider 的 <c>Load()</c>，
/// JSON provider 載入時會觸發全域 reload。
/// </summary>
public sealed class SystemParameterValidator
{
    private readonly IServiceProvider services;
    private readonly IConfigurationRoot root;
    private readonly SystemParameterConfigurationProvider provider;

    public SystemParameterValidator(IServiceProvider services, IConfiguration configuration, SystemParameterConfigurationProvider provider)
    {
        this.services = services;
        this.provider = provider;
        root = configuration as IConfigurationRoot
            ?? throw new InvalidOperationException("系統參數需要 IConfigurationRoot（ConfigurationManager）。");
        if (!root.Providers.Contains(provider))
        {
            throw new InvalidOperationException("系統參數的設定來源沒有加進設定：Program.cs 必須呼叫 builder.Configuration.AddSystemParameterOverrides()。");
        }
    }

    /// <summary>設定檔這一層的值（appsettings、環境變數、命令列…，不含系統參數覆寫）；都沒有時回 null。</summary>
    public string? GetBaseValue(string key)
    {
        foreach (var candidate in root.Providers.Reverse())
        {
            if (ReferenceEquals(candidate, provider))
            {
                continue;
            }

            if (candidate.TryGet(key, out var value) && value is not null)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// 以設定檔的值加上 <paramref name="overrides"/>（只看屬於這個區段的鍵）驗證整個區段，回傳錯誤訊息；空清單＝合法。
    /// 傳空的覆寫就是「設定檔本身是否合法」。
    /// </summary>
    public IReadOnlyList<string> Validate(string sectionName, IReadOnlyDictionary<string, string> overrides)
    {
        var definition = SystemParameterCatalog.All.First(x => string.Equals(x.SectionName, sectionName, StringComparison.OrdinalIgnoreCase));
        var prefix = sectionName + ":";
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in root.GetSection(sectionName).AsEnumerable())
        {
            if (value is null)
            {
                continue;
            }

            // 目前套用中的覆寫要換回設定檔的值，候選覆寫再疊上去。
            values[key] = provider.TryGet(key, out _) ? GetBaseValue(key) : value;
        }

        foreach (var (key, value) in overrides)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                values[key] = value;
            }
        }

        var candidate = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Where(x => x.Value is not null))
            .Build()
            .GetSection(sectionName);
        return definition.ValidateSection(services, candidate);
    }
}
