namespace CustomerHelpAgent.Web.Scheduling;

/// <summary>註冊排程作業（0.9.96 起）。</summary>
public static class ScheduledJobServiceCollectionExtensions
{
    /// <summary>
    /// 註冊一個排程作業：描述為 singleton（排程器、驗證器、管理頁讀它），作業類別為 scoped（每次執行在新的 scope 解析）。
    /// </summary>
    /// <param name="name">穩定的識別名稱（英文字母開頭、只含英文字母與數字）；用在設定鍵、鎖檔、執行紀錄與稽核，上線後不要改。</param>
    public static IServiceCollection AddScheduledJob<TJob>(this IServiceCollection services, string name, string displayName, string description, string defaultCron)
        where TJob : class, IScheduledJob
    {
        if (!ScheduledJobDescriptor.IsValidName(name))
        {
            throw new ArgumentException($"排程作業名稱「{name}」不合法：只能用英文字母開頭、英文字母與數字組成（最多 64 字）。", nameof(name));
        }

        if (!ScheduleCalculator.TryParse(defaultCron, out _))
        {
            throw new ArgumentException($"排程作業「{name}」的預設 cron「{defaultCron}」不是合法的 5 欄位 cron。", nameof(defaultCron));
        }

        services.AddScoped<TJob>();
        services.AddSingleton(new ScheduledJobDescriptor(name, displayName, description, defaultCron, typeof(TJob)));
        return services;
    }
}
