namespace CustomerHelpAgent.Web.Configuration.Parameters;

/// <summary>
/// 系統參數的設定來源（0.9.98 起）：疊在 appsettings、環境變數之上的一層覆寫。
///
/// 刻意是**被動**的：自己不讀資料庫（建立設定時資料庫還沒 migrate），由 <see cref="SystemParameterRuntime"/>
/// 在資料庫初始化之後、每次存檔後與每分鐘刷新時呼叫 <see cref="Apply"/>。
/// <see cref="Apply"/> 觸發 reload，所有以 <c>IOptionsMonitor</c> 讀設定的地方下一次讀取就拿到新值。
///
/// ⚠️ 每次 reload 都會讓所有 options 重新綁定 —— 只有內容真的變了才呼叫 <see cref="Apply"/>。
/// </summary>
public sealed class SystemParameterConfigurationProvider : ConfigurationProvider
{
    /// <summary>目前套用中的覆寫（鍵 → 值）。</summary>
    public IReadOnlyDictionary<string, string> Snapshot
    {
        get
        {
            // Apply 是整個換掉字典（參考指派是原子的），先取區域變數就不會讀到一半。
            var data = Data;
            return data.Where(x => x.Value is not null)
                .ToDictionary(x => x.Key, x => x.Value!, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>換上新的一組覆寫並通知設定已變更。</summary>
    public void Apply(IReadOnlyDictionary<string, string> overrides)
    {
        Data = overrides.ToDictionary(x => x.Key, x => (string?)x.Value, StringComparer.OrdinalIgnoreCase);
        OnReload();
    }
}

/// <summary>把同一個 <see cref="SystemParameterConfigurationProvider"/> 實例掛進設定（DI 也註冊同一個）。</summary>
public sealed class SystemParameterConfigurationSource(SystemParameterConfigurationProvider provider) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
}
