using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Share.Demo;

namespace CustomerHelpAgent.Web.Agents.AfterSales;

/// <summary>
/// 回放游標（1.0.5 起，Singleton，只放記憶體：重啟即清空）：每個會話、每條錄製 stream（<see cref="AgentRecordingStreams"/>，M4 起）
/// 一個 <see cref="ReplayCursor"/>，記下回放到錄製的第幾個顧客動作、第幾次模型呼叫。游標綁定錄製 Id，換了回放的錄製就從頭開始。
/// 顧客端 Agent 用 <c>customer</c>；AI 初審的照片、歷史、摘要節點各用自己的 stream（以申請的 CaseKey＝會話 SessionKey 為鍵），
/// 併行的節點各走各的游標。
/// ⚠️ 開新會話時 Runner 呼叫 <see cref="Reset"/>；一鍵重置示範資料（<see cref="DemoResetService"/>）成功後經
/// <see cref="IDemoResetParticipant"/> 清空全部游標。重啟後開著的會話游標從 0 開始，第一次模型呼叫的摘要就對不上而停止（回放中止）。
/// </summary>
public sealed class ReplayCursorStore : IDemoResetParticipant
{
    private readonly Dictionary<(Guid SessionKey, string Stream), ReplayCursor> cursors = [];
    private readonly Lock gate = new();

    /// <summary>取這個會話某條 stream（預設顧客端 Agent）回放指定錄製的游標；沒有、或原本回放的是另一段錄製時，建立一個從頭開始的新游標。</summary>
    public ReplayCursor Get(Guid sessionKey, int recordingId, string stream = AgentRecordingStreams.Customer)
    {
        lock (gate)
        {
            if (!cursors.TryGetValue((sessionKey, stream), out var cursor) || cursor.RecordingId != recordingId)
            {
                cursor = new ReplayCursor(recordingId);
                cursors[(sessionKey, stream)] = cursor;
            }

            return cursor;
        }
    }

    /// <summary>清掉這個會話所有 stream 的游標。</summary>
    public void Reset(Guid sessionKey)
    {
        lock (gate)
        {
            foreach (var key in cursors.Keys.Where(x => x.SessionKey == sessionKey).ToList())
            {
                cursors.Remove(key);
            }
        }
    }

    /// <summary>清空所有會話的游標（重置示範資料時：會話已全數刪除）。</summary>
    public void ClearAll()
    {
        lock (gate)
        {
            cursors.Clear();
        }
    }

    void IDemoResetParticipant.OnDemoDataReset() => ClearAll();
}

/// <summary>
/// 一個會話的回放位置。模型呼叫依錄製的 <c>(Sequence, Id)</c> 順序取用（呼叫沒有連到顧客動作，一個動作可能沒有任何呼叫），
/// 所以顧客動作與模型呼叫各自一個索引。
/// </summary>
public sealed class ReplayCursor(int recordingId)
{
    private readonly Lock gate = new();
    private int nextTurn;
    private int nextCall;

    public int RecordingId => recordingId;

    /// <summary>下一個待執行的顧客動作（錄製動作清單的索引）。</summary>
    public int NextTurn
    {
        get
        {
            lock (gate)
            {
                return nextTurn;
            }
        }
    }

    /// <summary>下一筆要送出的模型回應（錄製呼叫清單的索引）。</summary>
    public int NextCall
    {
        get
        {
            lock (gate)
            {
                return nextCall;
            }
        }
    }

    internal void AdvanceTurn()
    {
        lock (gate)
        {
            nextTurn++;
        }
    }

    internal void AdvanceCall()
    {
        lock (gate)
        {
            nextCall++;
        }
    }
}
