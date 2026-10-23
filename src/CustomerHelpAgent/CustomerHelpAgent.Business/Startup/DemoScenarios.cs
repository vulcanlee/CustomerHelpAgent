using CustomerHelpAgent.Share.Demo;

namespace CustomerHelpAgent.Business.Startup;

/// <summary>
/// 展示劇本（1.0.5 起，只有資料）：展示控制台逐一列出，「準備此劇本」依序重置示範資料、只開這個劇本的故障、
/// 選定模式，再切換成 <see cref="DemoScenario.Account"/> 到線上客服。
/// A、B 的顧客台詞取自設計文件 §3.2 的旗艦劇本，錄製與回放都以這幾句為準 —— 改字會讓既有的金劇本對不上（回放中止），
/// 請同時重新錄製。<see cref="DemoScenario.Code"/> 也是金劇本的劇本代碼（存進錄製檔），既有代碼不要改名。
/// </summary>
public static class DemoScenarios
{
    public static IReadOnlyList<DemoScenario> All { get; } =
    [
        new(
            "A",
            "換尺碼（旗艦）",
            DemoSeedData.Customer1,
            ["剛買的鞋尺碼不合適，想換大一碼", "黑色那雙，還沒穿過"],
            [],
            [
                "顧客有兩雙鞋，Agent 不猜是哪一雙，先停下來問「黑 40 還是白 41？穿過沒有？」。",
                "簽收天數由程式計算、庫存即時查，規則卡顯示條文的來源與版本。",
                "送出前出現確認卡（內容由程式重新查出），按「確認送出」後才寫入並回傳申請編號。",
            ]),
        new(
            "B",
            "一句兩事（旗艦）",
            DemoSeedData.Customer1,
            ["這雙想換大一碼，另一雙開膠了也幫我看看", "黑的換大一碼，白的開膠"],
            [],
            [
                "一句話裡有兩件事，Agent 先問清楚哪雙換碼、哪雙開膠。",
                "黑鞋照換碼規則繼續辦；白鞋屬品質問題，請顧客先補開膠照片，停下來等上傳、不空轉。",
            ]),
        new(
            "C",
            "退貨逾期",
            DemoSeedData.Customer2,
            ["上次買的外套想退貨", "藏青色那件，還沒穿過"],
            [],
            [
                "外套已簽收 10 天，超過退貨規則的 7 天。",
                "Agent 依規則卡說明不符資格，不自行通融，提供轉人工客服。",
            ]),
        new(
            "D",
            "物流催件",
            DemoSeedData.Customer2,
            ["我的外套怎麼還沒到？幫我催一下"],
            [],
            [
                "灰色外套已出貨 4 天尚未送達，Agent 查物流軌跡、由程式判定可以催件，再經確認卡送出催件申請。",
                "想示範配送異常時，準備好之後再到「故障模擬」打開「物流異常」。",
            ]),
        new(
            "F1",
            "訂單系統逾時",
            DemoSeedData.Customer1,
            ["剛買的鞋尺碼不合適，想換大一碼"],
            [DemoFault.OrderServiceTimeout],
            [
                "查訂單要等一陣子才失敗；程式停止這一輪並顯示固定說明。",
                "說明必須包含「不代表您沒有這筆訂單」——逾時不等於查無。",
            ]),
        new(
            "F2",
            "權限過期",
            DemoSeedData.Customer1,
            ["剛買的鞋尺碼不合適，想換大一碼"],
            [DemoFault.PermissionExpired],
            [
                "工具的身分檢查不通過，程式立即停止，模型沒有機會改寫說明。",
                "只影響 Agent；同一時間傳統頁面照常可用。",
            ]),
        new(
            "F3",
            "規則服務停擺",
            DemoSeedData.Customer1,
            ["剛買的鞋尺碼不合適，想換大一碼", "黑色那雙，還沒穿過"],
            [DemoFault.PolicyServiceUnavailable],
            [
                "訂單查得到，但查規則失敗；Agent 明說暫時無法核實，不憑印象回答。",
            ]),
        new(
            "F4",
            "規則衝突",
            DemoSeedData.Customer1,
            ["剛買的鞋尺碼不合適，想換大一碼", "黑色那雙，還沒穿過"],
            [DemoFault.PolicyConflict],
            [
                "同時查到兩個互相衝突的規則版本，程式封鎖寫入工具。",
                "不會出現確認卡，Agent 只能轉人工。",
            ]),
        new(
            "F5",
            "無適用規則",
            DemoSeedData.Customer1,
            ["剛買的鞋尺碼不合適，想換大一碼", "黑色那雙，還沒穿過"],
            [DemoFault.PolicyNotFound],
            [
                "查不到適用的售後規則，Agent 不自己編規則，改為轉人工。",
            ]),
    ];
}

/// <summary>一個展示劇本。</summary>
/// <param name="Code">劇本代碼（也是金劇本的劇本代碼）。</param>
/// <param name="Title">畫面上的名稱。</param>
/// <param name="Account">切換成哪個示範帳號（<see cref="DemoSeedData.Accounts"/>）。</param>
/// <param name="CustomerLines">顧客依序要說的話（回放時也是依序帶入）。</param>
/// <param name="Faults">這個劇本要開的故障；準備時其他故障一律關閉。</param>
/// <param name="TalkingPoints">展示者的講解重點。</param>
public sealed record DemoScenario(
    string Code,
    string Title,
    string Account,
    IReadOnlyList<string> CustomerLines,
    IReadOnlyList<DemoFault> Faults,
    IReadOnlyList<string> TalkingPoints);
