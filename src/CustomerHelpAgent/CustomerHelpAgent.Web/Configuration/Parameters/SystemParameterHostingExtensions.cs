using CustomerHelpAgent.Business.Services.DataAccess;

namespace CustomerHelpAgent.Web.Configuration.Parameters;

/// <summary>系統參數（0.9.98 起）的設定來源與服務註冊。</summary>
public static class SystemParameterHostingExtensions
{
    /// <summary>
    /// 把系統參數覆寫層加進設定，回傳同一個 provider 實例交給 <see cref="AddSystemParameters"/>。
    ///
    /// ⚠️ 必須是 Program.cs 最後加入的設定來源：最後加入的優先權最高，覆寫才蓋得過 appsettings、環境變數與命令列。
    /// （<c>WebApplicationFactory</c> 在之後加入的測試設定仍會蓋過它。）
    /// </summary>
    public static SystemParameterConfigurationProvider AddSystemParameterOverrides(this IConfigurationBuilder configuration)
    {
        var provider = new SystemParameterConfigurationProvider();
        configuration.Add(new SystemParameterConfigurationSource(provider));
        return provider;
    }

    /// <summary>
    /// 註冊系統參數的服務與每分鐘刷新的背景工作。
    /// ⚠️ 必須在 <c>ExceptionLogWriter</c>（<c>AddApplicationServices</c>）之後：主機以相反順序停止，刷新失敗的錯誤才寫得進系統例外紀錄。
    /// </summary>
    public static IServiceCollection AddSystemParameters(this IServiceCollection services, SystemParameterConfigurationProvider provider)
    {
        services.AddSingleton(provider);
        services.AddSingleton<SystemParameterService>();
        services.AddSingleton<SystemParameterValidator>();
        services.AddSingleton<SystemParameterRuntime>();
        services.AddSingleton<SystemParameterManager>();
        services.AddHostedService<SystemParameterRefreshWorker>();
        return services;
    }
}
