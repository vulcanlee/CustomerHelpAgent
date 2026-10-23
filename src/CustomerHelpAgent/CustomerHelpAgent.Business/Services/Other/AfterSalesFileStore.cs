using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// 售後申請照片實體檔的寫入、解析與刪除（比照 <see cref="ProjectFileStore"/>）。
/// 相對路徑一律是 <c>yyyy/MM/{guid:N}{ext}</c>；解析時拒絕跑出 <c>AfterSalesFilePath</c> 根目錄的路徑。
/// 副檔名白名單由呼叫端（<c>AfterSalesRequestService</c>）先驗證。
/// </summary>
public class AfterSalesFileStore
{
    private readonly IOptions<SystemSettings> systemSettingsOptions;
    private readonly ILogger<AfterSalesFileStore> logger;

    public AfterSalesFileStore(IOptions<SystemSettings> systemSettingsOptions, ILogger<AfterSalesFileStore> logger)
    {
        this.systemSettingsOptions = systemSettingsOptions;
        this.logger = logger;
    }

    private string RootPath => systemSettingsOptions.Value.ExternalFileSystem.AfterSalesFilePath;

    /// <summary>寫入一個照片檔並回傳相對路徑。寫到一半失敗會先刪掉殘檔再丟出例外。</summary>
    public async Task<string> SaveAsync(Stream content, string extension, DateTime now)
    {
        var relativePath = string.Create(CultureInfo.InvariantCulture, $"{now:yyyy}/{now:MM}/{Guid.NewGuid():N}{extension}");
        var fullPath = TryResolveFullPath(relativePath)
            ?? throw new InvalidOperationException("After-sales file root is not configured or the path is invalid.");

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        try
        {
            await using var target = File.Create(fullPath);
            await content.CopyToAsync(target);
        }
        catch
        {
            Delete(relativePath);
            throw;
        }

        logger.LogDebug("After-sales file saved. RelativePath={RelativePath}", relativePath);
        return relativePath;
    }

    /// <summary>
    /// 把相對路徑解析成完整路徑；解析後**不在**根目錄之下（或根目錄未設定）時回 null。
    /// 根目錄未設定時一律拒絕：<c>Path.Combine("", x)</c> 會退化成相對於工作目錄的路徑。
    /// </summary>
    public string? TryResolveFullPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(RootPath) || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath));
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    /// <summary>刪除一個照片檔。檔案不存在視為成功；路徑跑出根目錄或刪不掉時記警告、回 false，不丟例外。</summary>
    public bool Delete(string? relativePath)
    {
        var fullPath = TryResolveFullPath(relativePath);
        if (fullPath is null)
        {
            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                logger.LogWarning("After-sales file deletion refused because the path escapes the configured root. RelativePath={RelativePath}", relativePath);
            }

            return false;
        }

        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete after-sales file. RelativePath={RelativePath}", relativePath);
            return false;
        }
    }
}
