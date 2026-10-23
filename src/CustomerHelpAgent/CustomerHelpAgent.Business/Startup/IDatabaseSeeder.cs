namespace CustomerHelpAgent.Business.Startup;

/// <summary>
/// 啟動時寫入種子資料的一個步驟。由 <see cref="IDatabaseInitializer"/> 在 migration 之後依 <see cref="Order"/> 由小到大執行。
///
/// 衍生專案要加自己的種子資料（例如預設的代碼、範例資料），就實作這個介面並註冊成 Scoped，
/// 不要再回到 Program.cs 裡寫 —— 那裡沒有測試，也沒有跨行程鎖保護。
///
/// 實作要點：
/// <list type="bullet">
/// <item>必須<b>冪等</b>：每次啟動都會執行，資料已存在時不得重複新增，也不得覆寫使用者後來改過的內容。</item>
/// <item>注入 Scoped 的 <c>BackendDBContext</c>（與其他 seeder 共用同一個 instance），返回前自行 <c>SaveChangesAsync</c>；
/// 初始化器會在每個 seeder 之後清空追蹤狀態，不要依賴前一個 seeder 留下的實體。</item>
/// <item><see cref="Order"/> 不可與其他 seeder 重複（重複時啟動失敗）。內建：10 預設角色、20 support 帳號、30 RBAC 回填、40 匯入舊的 AI 提示詞設定、50 售後展示示範帳號、60 售後展示示範商品與訂單。</item>
/// </list>
/// </summary>
public interface IDatabaseSeeder
{
    /// <summary>執行順序，小的先執行；不可重複。</summary>
    int Order { get; }

    /// <summary>出現在啟動日誌裡的名稱。</summary>
    string Name { get; }

    Task SeedAsync(CancellationToken cancellationToken);
}
