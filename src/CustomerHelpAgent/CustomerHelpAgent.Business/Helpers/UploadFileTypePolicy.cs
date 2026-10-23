namespace CustomerHelpAgent.Business.Helpers;

/// <summary>
/// 上傳檔案的類型政策：**副檔名白名單**，同時也是 ContentType 的權威來源。
///
/// 為什麼需要白名單：儲存檔名雖然已改為 GUID（不會有路徑穿越），但副檔名會被保留，
/// 且下載時會回吐 ContentType。若允許 <c>.html</c> / <c>.svg</c> 並沿用呼叫端提供的
/// ContentType，等於在自己的網域上開一個 stored XSS。
///
/// 為什麼 ContentType 由這裡決定：呼叫端提供的 <c>ContentType</c> 完全不可信
/// —— 攻擊者可以上傳 <c>.txt</c> 卻宣稱是 <c>text/html</c>。
/// 一律依副檔名對應，不看呼叫端說什麼。
///
/// 各案可於 <c>SystemSettings.Upload.AllowedExtensions</c> 覆寫白名單；留空則採用此處預設。
/// </summary>
public static class UploadFileTypePolicy
{
    public const string DefaultContentType = "application/octet-stream";

    /// <summary>
    /// 預設允許的副檔名 → 正規 ContentType。
    ///
    /// 刻意**不包含** .html/.htm/.svg/.xhtml（可執行 script，同源 XSS）
    /// 與 .exe/.dll/.bat/.cmd/.ps1/.sh/.js（可執行檔）。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> DefaultContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "application/pdf",
            [".txt"] = "text/plain",
            [".csv"] = "text/csv",
            [".doc"] = "application/msword",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xls"] = "application/vnd.ms-excel",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".ppt"] = "application/vnd.ms-powerpoint",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".gif"] = "image/gif",
            [".bmp"] = "image/bmp",
            [".webp"] = "image/webp",
            [".zip"] = "application/zip",
            [".7z"] = "application/x-7z-compressed",
            [".rar"] = "application/vnd.rar",
        };

    /// <summary>預設白名單（顯示於錯誤訊息與文件）。</summary>
    public static IReadOnlyCollection<string> DefaultAllowedExtensions => (List<string>)[.. DefaultContentTypes.Keys];

    public static bool IsAllowed(string? fileName, IReadOnlyCollection<string>? allowedExtensions = null)
    {
        var extension = GetExtension(fileName);
        if (extension.Length == 0)
        {
            return false;
        }

        return Normalize(allowedExtensions).Contains(extension);
    }

    /// <summary>
    /// 依副檔名決定 ContentType。呼叫端提供的值一律忽略。
    /// 白名單被覆寫成預設清單沒有的副檔名時，回退為 octet-stream（瀏覽器不會內嵌執行）。
    /// </summary>
    public static string ResolveContentType(string? fileName)
    {
        var extension = GetExtension(fileName);
        return DefaultContentTypes.TryGetValue(extension, out var contentType)
            ? contentType
            : DefaultContentType;
    }

    private static HashSet<string> Normalize(IReadOnlyCollection<string>? allowedExtensions)
    {
        if (allowedExtensions is null || allowedExtensions.Count == 0)
        {
            // 一定要帶 OrdinalIgnoreCase：集合運算式 [.. keys] 會建出使用預設
            // （區分大小寫）比較器的 HashSet，讓 ".PNG" 這種寫法被誤擋。
            return new HashSet<string>(DefaultContentTypes.Keys, StringComparer.OrdinalIgnoreCase);
        }

        return allowedExtensions
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().StartsWith('.') ? x.Trim() : "." + x.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string GetExtension(string? fileName)
    {
        return string.IsNullOrWhiteSpace(fileName)
            ? string.Empty
            : Path.GetExtension(fileName).Trim();
    }
}
