using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// Token 用量紀錄的原始 usage JSON 存放。
///
/// ⚠️ <b>檔案生命週期的唯一入口。</b>三條刪除路徑（刪單筆、清除此日之前、清空全部）
/// 都必須經過這裡，不可有人自己去 <c>File.Delete</c>。作法與
/// <see cref="ExceptionStackFileStore"/> 一致，理由也一樣：專案附件曾因
/// 「資料表紀錄由 EF Cascade 處理、實體檔案卻要 Service 層自己記得刪」而留下孤兒檔。
///
/// 相對路徑為 <c>{yyyyMM}/{GUID}.json</c>。每次呼叫都是獨立一筆、不像例外紀錄會合併，
/// 所以用 GUID 而非雜湊。
/// </summary>
public class TokenUsageRawStore
{
    private readonly IOptions<SystemSettings> systemSettingsOptions;
    private readonly ILogger<TokenUsageRawStore> logger;

    public TokenUsageRawStore(
        IOptions<SystemSettings> systemSettingsOptions,
        ILogger<TokenUsageRawStore> logger)
    {
        this.systemSettingsOptions = systemSettingsOptions;
        this.logger = logger;
    }

    private string RootPath => systemSettingsOptions.Value.ExternalFileSystem.TokenUsagePath;

    /// <summary>
    /// 寫入原始 usage JSON。
    /// </summary>
    /// <returns>成功時回傳相對路徑；失敗回 null（<b>不得</b>讓資料列因此建不起來）。</returns>
    public async Task<string?> WriteAsync(DateTime occurredAt, string? content)
    {
        if (string.IsNullOrWhiteSpace(RootPath) || string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var relativePath = $"{occurredAt:yyyyMM}/{Guid.NewGuid():N}.json";

        try
        {
            var fullPath = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory) == false)
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullPath, content);
            return relativePath;
        }
        catch (Exception ex)
        {
            // 寫檔失敗只是少了原始明細，資料列仍必須建立。
            logger.LogWarning(ex, "Failed to write usage payload file. Path={RelativePath}", relativePath);
            return null;
        }
    }

    /// <summary>讀取原始 JSON。檔案不存在或讀取失敗一律回 null，由呼叫端顯示友善訊息。</summary>
    public async Task<string?> ReadAsync(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(RootPath) || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            var fullPath = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(fullPath) == false ? null : await File.ReadAllTextAsync(fullPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read usage payload file. Path={RelativePath}", relativePath);
            return null;
        }
    }

    /// <summary>刪除單一檔案。檔案不存在視為成功。</summary>
    public void Delete(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(RootPath) || string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        try
        {
            var fullPath = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception ex)
        {
            // 刪不掉就留著，不影響資料列刪除。
            logger.LogWarning(ex, "Failed to delete usage payload file. Path={RelativePath}", relativePath);
        }
    }

    /// <summary>
    /// 清空整個目錄（供「清空全部」使用）。
    /// 刻意刪內容而非刪目錄本身，因為目錄由 Program.cs 啟動時建立，刪掉就不會再有人補。
    /// </summary>
    public void DeleteAll()
    {
        if (string.IsNullOrWhiteSpace(RootPath) || Directory.Exists(RootPath) == false)
        {
            return;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(RootPath))
            {
                Directory.Delete(directory, recursive: true);
            }

            foreach (var file in Directory.EnumerateFiles(RootPath))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            // 同上。
            logger.LogWarning(ex, "Failed to clear the usage payload directory.");
        }
    }

    /// <summary>
    /// 刪除早於 <paramref name="threshold"/> 所在月份的整個 <c>{yyyyMM}</c> 目錄（0.9.96 起，自動過期的收尾）。
    ///
    /// 檔案依紀錄的 <c>OccurredAt</c> 分月存放，所以門檻月份之前的目錄只剩：逐筆刪檔後的空目錄、
    /// 「檔已寫、資料列沒建起來」或刪除中途被中斷留下的孤兒檔。門檻所在月份本身不動 —— 那個月可能還有未過期的紀錄。
    /// 只能在該門檻之前的資料列都已刪除之後呼叫。
    /// </summary>
    public void DeleteMonthsBefore(DateTime threshold)
    {
        if (string.IsNullOrWhiteSpace(RootPath) || Directory.Exists(RootPath) == false)
        {
            return;
        }

        var boundary = new DateTime(threshold.Year, threshold.Month, 1);

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(RootPath))
            {
                var name = Path.GetFileName(directory);
                if (DateTime.TryParseExact(name, "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
                    && month < boundary)
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove expired usage payload folders.");
        }
    }
}
