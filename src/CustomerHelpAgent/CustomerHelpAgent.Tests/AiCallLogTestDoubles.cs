using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Tests;

/// <summary>AI 對話紀錄的假記錄器：只把收到的項目留下來，不碰資料庫也不碰檔案系統。</summary>
internal sealed class FakeAiCallLogRecorder : IAiCallLogRecorder
{
    public bool IsEnabled { get; set; } = true;

    public List<AiCallLogEntry> Entries { get; } = [];

    public Task RecordAsync(AiCallLogEntry entry)
    {
        if (IsEnabled)
        {
            Entries.Add(entry);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// 違約的記錄器：<see cref="RecordAsync"/> 一律丟例外。
/// 守住「記錄點在 finally 裡，丟出的例外會蓋掉原本的回傳值」這個踩雷點（速查 §6.7）。
/// </summary>
internal sealed class ThrowingAiCallLogRecorder : IAiCallLogRecorder
{
    public bool IsEnabled => true;

    public Task RecordAsync(AiCallLogEntry entry)
        => throw new InvalidOperationException("recorder failure");
}
