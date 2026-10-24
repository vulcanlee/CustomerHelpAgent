using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Demo;

namespace CustomerHelpAgent.Business.Startup;

/// <summary>
/// 展示劇本（1.0.5 起，只有資料）：展示控制台逐一列出，「準備此劇本」依序重置示範資料、只開這個劇本的故障、
/// 選定模式，再切換成 <see cref="DemoScenario.Account"/> 到線上客服（<see cref="DemoScenario.Mode"/> 是多 Agent 時帶 <c>?mode=handoff</c>）。
/// A、B 的顧客台詞取自設計文件 §3.2 的旗艦劇本，錄製與回放都以這幾句為準 —— 改字會讓既有的金劇本對不上（回放中止），
/// 請同時重新錄製。<see cref="DemoScenario.Code"/> 也是金劇本的劇本代碼（存進錄製檔），既有代碼不要改名。
/// M5：H 是 B 的多 Agent 版本（台詞相同，金劇本要以多 Agent 模式錄）；E（長期記憶）跨三段對話——回放一次只播一段會話，
/// 所以 E 的金劇本只涵蓋第一段（記住偏好），之後的開新對話與刪除記憶請用即時模式示範。
/// </summary>
public static class DemoScenarios
{
    public static IReadOnlyList<DemoScenario> All { get; } =
    [
        new(
            "A",
            "換尺碼（旗艦）",
            "換尺碼",
            ScenarioFlow.SingleAgent,
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
            "一句兩事",
            ScenarioFlow.SingleAgent,
            DemoSeedData.Customer1,
            ["這雙想換大一碼，另一雙開膠了也幫我看看", "黑的換大一碼，白的開膠"],
            [],
            [
                "一句話裡有兩件事，Agent 先問清楚哪雙換碼、哪雙開膠。",
                "黑鞋照換碼規則繼續辦；白鞋屬品質問題，請顧客先補開膠照片，停下來等上傳、不空轉。",
            ]),
        new(
            "H",
            "多 Agent：一句兩事",
            "一句兩事",
            ScenarioFlow.MultiAgent,
            DemoSeedData.Customer1,
            ["這雙想換大一碼，另一雙開膠了也幫我看看", "黑的換大一碼，白的開膠"],
            [],
            [
                "與劇本 B 同樣的兩句話，改由多 Agent 處理：分流 Agent 判斷要先找誰，不自己查資料。",
                "分流 Agent 先轉給換退貨專員：專員查換碼規則、處理黑鞋換大一碼。",
                "換退貨專員處理完交回分流 Agent，分流再轉給品質專員處理白鞋開膠（請顧客補開膠照片）。",
                "每則回覆標示發言的專員；幕後面板的交接列寫出「誰 → 誰：理由」。每位專員只拿得到自己的工具，安全規則與單一 Agent 相同。",
            ],
            AgentMode.Handoff),
        new(
            "A-T",
            "傳統表單：換尺碼",
            "換尺碼",
            ScenarioFlow.Traditional,
            DemoSeedData.Customer1,
            [
                "在售後申請表單選擇黑色那雙，申請類型選「換尺碼」並選大一碼",
                "商品是否穿過選「未穿過」，問題描述填尺寸不合，按「送出申請」",
            ],
            [],
            [
                "傳統流程：顧客自己填表，沒有 AI 協助；準備後會直接開啟售後申請表單。",
                "與劇本 A 是同一個情境（黑鞋換大一碼），可並排比較步驟、時間與成本。",
                "送出後由客服人員在售後審核頁處理，再與 Agent 流程的結果對照。",
            ]),
        new(
            "B-T",
            "傳統表單：一句兩事",
            "一句兩事",
            ScenarioFlow.Traditional,
            DemoSeedData.Customer1,
            [
                "在售後申請表單填第一張表：選擇黑色那雙，申請類型選「換尺碼」並選大一碼，填寫未穿過與問題描述，送出申請",
                "送出後按「回到我的訂單」，在白色那雙按「申請售後」開第二張表：申請類型選「品質問題（開膠等）」，上傳開膠照片、填寫問題描述，送出申請",
            ],
            [],
            [
                "傳統流程：一句話的兩件事要填兩張表，各自選訂單商品、原因與照片。",
                "與劇本 B、H 是同一個情境，可並排比較單一 Agent、多 Agent 與傳統表單。",
            ]),
        new(
            "A-M",
            "多 Agent：換尺碼",
            "換尺碼",
            ScenarioFlow.MultiAgent,
            DemoSeedData.Customer1,
            ["剛買的鞋尺碼不合適，想換大一碼", "黑色那雙，還沒穿過"],
            [],
            [
                "與劇本 A 同樣的兩句話，改由多 Agent 處理：分流 Agent 轉給換退貨專員。",
                "與劇本 A、A-T 是同一個情境，可並排比較三種做法。",
            ],
            AgentMode.Handoff),
        new(
            "C",
            "退貨逾期",
            "退貨",
            ScenarioFlow.SingleAgent,
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
            "物流催件",
            ScenarioFlow.SingleAgent,
            DemoSeedData.Customer2,
            ["我的外套怎麼還沒到？幫我催一下"],
            [],
            [
                "灰色外套已出貨 4 天尚未送達，Agent 查物流軌跡、由程式判定可以催件，再經確認卡送出催件申請。",
                "想示範配送異常時，準備好之後再到「故障模擬」打開「物流異常」。",
            ]),
        new(
            "E",
            "長期記憶",
            "長期記憶",
            ScenarioFlow.SingleAgent,
            DemoSeedData.Customer1,
            ["我平常鞋子穿 41 碼，以後記得喔", "想換一雙鞋的尺寸", "想換一雙鞋的尺寸"],
            [],
            [
                "第 1 段對話：說第一句，出現記憶確認卡（鞋碼偏好 41 碼），按下確認才會記住；按取消就什麼都不寫入。",
                "按「開新對話」說第二句：Agent 帶出「記得您平常穿 41 碼，這次也是嗎？」——記憶只是偏好，仍要跟顧客確認，不當成訂單事實。",
                "到「我的 AI 記憶」頁刪除這筆鞋碼偏好。",
                "再按「開新對話」說第三句（與第二句相同）：Agent 不再提起 41 碼，刪除後下一次呼叫立即不再注入。",
                "回放只播一段會話：金劇本只涵蓋第 1 段，後面三步請用即時模式示範。",
            ]),
        new(
            "F1",
            "訂單系統逾時",
            "故障模擬",
            ScenarioFlow.SingleAgent,
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
            "故障模擬",
            ScenarioFlow.SingleAgent,
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
            "故障模擬",
            ScenarioFlow.SingleAgent,
            DemoSeedData.Customer1,
            ["剛買的鞋尺碼不合適，想換大一碼", "黑色那雙，還沒穿過"],
            [DemoFault.PolicyServiceUnavailable],
            [
                "訂單查得到，但查規則失敗；Agent 明說暫時無法核實，不憑印象回答。",
            ]),
        new(
            "F4",
            "規則衝突",
            "故障模擬",
            ScenarioFlow.SingleAgent,
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
            "故障模擬",
            ScenarioFlow.SingleAgent,
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
/// <param name="Family">情境家族（M6）：同一情境的不同做法同家族，例如「換尺碼」「一句兩事」，效益對照頁依此並排。</param>
/// <param name="Flow">處理流程（M6）：傳統表單、單一 Agent 或多 Agent。</param>
/// <param name="Account">切換成哪個示範帳號（<see cref="DemoSeedData.Accounts"/>）。</param>
/// <param name="CustomerLines">顧客依序要說的話（回放時也是依序帶入）。</param>
/// <param name="Faults">這個劇本要開的故障；準備時其他故障一律關閉。</param>
/// <param name="TalkingPoints">展示者的講解重點。</param>
/// <param name="Mode">線上客服的 Agent 模式（M5）：多 Agent 時準備劇本後導向 <c>/shop/assistant?mode=handoff</c>；預設單一 Agent。</param>
public sealed record DemoScenario(
    string Code,
    string Title,
    string Family,
    ScenarioFlow Flow,
    string Account,
    IReadOnlyList<string> CustomerLines,
    IReadOnlyList<DemoFault> Faults,
    IReadOnlyList<string> TalkingPoints,
    AgentMode Mode = AgentMode.Single);
