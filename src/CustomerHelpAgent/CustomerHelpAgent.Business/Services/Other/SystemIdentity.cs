using Microsoft.Extensions.Options;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>系統名稱、簡介與版本（0.9.98 起）。名稱與簡介可在「系統參數」頁修改，讀到的永遠是目前的值。</summary>
public interface ISystemIdentity
{
    string Name { get; }

    string Description { get; }

    string Version { get; }
}

/// <summary>
/// ⚠️ <b>讀系統名稱與簡介的唯一入口</b>（<c>SystemParameterTests</c> 守門）：以前各處注入 <c>IOptions&lt;SystemSettings&gt;</c>，
/// 那是啟動時的快照，改了要重啟才生效。
///
/// 讀 <see cref="IOptionsMonitor{TOptions}"/>；若執行中有人把 appsettings 改壞（例如路徑變成相對路徑），
/// <c>CurrentValue</c> 會丟驗證例外 —— 這裡改回傳上一次讀到的正確值，側邊欄、登入頁與告警信不會因此壞掉
/// （改壞的設定由啟動驗證與系統參數頁回報）。
///
/// ⚠️ 不可注入 <c>ILogger</c>：例外告警服務在例外記錄管線內使用它，記日誌會形成遞迴（速查表 §6.6）。
/// </summary>
public sealed class SystemIdentity : ISystemIdentity
{
    private readonly IOptionsMonitor<SystemSettings> options;
    private SystemInformation lastGood = new();

    public SystemIdentity(IOptionsMonitor<SystemSettings> options)
    {
        this.options = options;
    }

    public string Name => Current.SystemName;

    public string Description => Current.SystemDescription;

    public string Version => Current.SystemVersion;

    private SystemInformation Current
    {
        get
        {
            try
            {
                return lastGood = options.CurrentValue.SystemInformation;
            }
            catch (OptionsValidationException)
            {
                // 沿用上一次的正確值（改壞的設定由啟動驗證與系統參數頁回報，這裡不記日誌，原因見類別說明）。
                return lastGood;
            }
        }
    }
}
