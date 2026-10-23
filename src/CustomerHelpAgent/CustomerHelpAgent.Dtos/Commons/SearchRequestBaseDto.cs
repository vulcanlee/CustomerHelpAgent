using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace CustomerHelpAgent.Dtos.Commons;

/// <summary>
/// 搜尋請求基礎參數 (提供分頁、排序、關鍵字搜尋等通用功能)
/// </summary>
public class SearchRequestBaseDto
{
    /// <summary>
    /// 每頁筆數上限。避免單次請求把整張表撈進記憶體。
    /// </summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// 頁碼 (從 1 開始)
    /// </summary>
    /// <remarks>
    /// 下限必須是 1：Repository 以 <c>Skip((PageIndex - 1) * PageSize)</c> 分頁，
    /// 傳 0 會變成 <c>Skip(-PageSize)</c> 而丟例外。
    /// </remarks>
    [Range(1, int.MaxValue, ErrorMessage = "頁碼必須大於或等於 1。")]
    public int PageIndex { get; set; } = 1;

    /// <summary>
    /// 每頁筆數
    /// </summary>
    [Range(1, MaxPageSize, ErrorMessage = "每頁筆數必須介於 1 到 200 之間。")]
    public int PageSize { get; set; } = 10;

    /// <summary>
    /// 關鍵字搜尋 (名稱、描述)
    /// </summary>
    public string? Keyword { get; set; }

    /// <summary>
    /// 排序欄位 (name, startdate, enddate, status, priority, completionpercentage, createdat)
    /// </summary>
    public string? SortBy { get; set; }

    /// <summary>
    /// 是否降冪排序
    /// </summary>
    public bool SortDescending { get; set; } = false;

    /// <summary>
    /// 是否包含關聯資料
    /// </summary>
    public bool IncludeRelatedData { get; set; } = false;
}
