using System.Text.Json;
using System.Text.Json.Serialization;

namespace CustomerHelpAgent.Web.Components.Commons;

/// <summary>
/// 表單對話窗的「未儲存變更」偵測：開窗時 <see cref="Capture"/> 一份快照，
/// 按下取消／確定時 <see cref="IsDirty"/> 與當下狀態做**值比對**。
/// 改了又改回原值視為無變更，不會白問使用者一次。
///
/// 為什麼不用 <c>EditContext.IsModified()</c>：只有繼承 <c>InputBase&lt;T&gt;</c> 的 Blazor 內建元件
/// 才會呼叫 <c>EditContext.NotifyFieldChanged</c>。本專案的欄位全是 AntDesign 元件
/// （<c>Input</c>／<c>Select</c>／<c>Switch</c>／<c>Checkbox</c>／<c>DatePicker</c>）直接綁模型屬性，
/// 外層 <c>EditContext</c> 一輩子收不到欄位變更通知 —— <c>IsModified()</c> 會恆為 false，
/// 未儲存提示**永遠不跳**，而且不會有任何錯誤或徵兆。
/// （<c>Validate()</c> 之所以能用，是因為 <c>DataAnnotationsValidator</c> 在當下整包重驗，與欄位通知無關。）
///
/// 為什麼不讓 AdapterModel 實作 <c>IEquatable</c>：8 個模型都要手寫 List 的 SequenceEqual，
/// 漏一個欄位就是靜默失效，而新模型永遠會有人忘記。序列化比對不需要模型配合。
///
/// ⚠️ 快照字串含 Password 等敏感欄位，**絕不可寫進 log**。
/// 本類別刻意不注入 ILogger，就是為了讓「順手 log 一下快照」這件事不方便做。
/// </summary>
public sealed class FormDirtyTracker
{
    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        // 模型之間若長出雙向參考（例如日後補上 RoleView.Users），序列化會直接丟例外；
        // 變更偵測不該因為模型多了一個關聯就讓整頁壞掉。
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    private string? snapshot;
    private Func<string>? extraState;

    /// <summary>
    /// 開窗前的**最後一步**呼叫：把「使用者還沒動過」的狀態存成快照。
    /// 任何預設值、關聯資料都要先塞完再呼叫，否則那些值會被當成使用者的變更。
    /// </summary>
    /// <param name="model">要比對的模型（通常是 <c>CurrentRecord</c>）。</param>
    /// <param name="extraState">
    /// 不在模型裡、但使用者會認為也是「變更」的暫存狀態指紋，
    /// 例如待上傳檔案與待刪除檔案 ID。可為 null。
    /// </param>
    public void Capture(object? model, Func<string>? extraState = null)
    {
        this.extraState = extraState;
        snapshot = Serialize(model, extraState);
    }

    /// <summary>
    /// 與快照比對。沒有快照（忘了呼叫 <see cref="Capture"/>）時一律當成「有變更」——
    /// 寧可多問使用者一次，也不要靜默丟掉整窗輸入。
    /// </summary>
    public bool IsDirty(object? model)
        => snapshot is null
           || string.Equals(snapshot, Serialize(model, extraState), StringComparison.Ordinal) == false;

    /// <summary>關窗後清掉快照，避免下次開窗前的空窗期誤判。</summary>
    public void Clear()
    {
        snapshot = null;
        extraState = null;
    }

    private static string Serialize(object? model, Func<string>? extraState)
    {
        string modelJson;
        try
        {
            // ⚠️ 一定要傳執行時型別：以 object 為靜態型別序列化只會吐出 "{}"，
            // 那樣每次比對都相等，變更偵測會整組失效且毫無徵兆。
            modelJson = JsonSerializer.Serialize(model, model?.GetType() ?? typeof(object), SnapshotOptions);
        }
        catch (NotSupportedException)
        {
            // 無法序列化時，安全的方向是「當成有變更」：每次回傳不同字串。
            return Guid.NewGuid().ToString("N");
        }

        return extraState is null ? modelJson : string.Concat(modelJson, "", extraState());
    }
}
