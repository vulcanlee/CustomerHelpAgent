using AntDesign;
using Microsoft.AspNetCore.Components.Forms;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Components.Views.Shop;

/// <summary>選好、還沒送出的照片（已讀進記憶體）。</summary>
internal sealed record ShopPendingPhoto(Guid Id, string Name, long Size, byte[] Content);

/// <summary>
/// 售後表單與補件窗共用的選照片流程。選檔當下就把內容讀進記憶體：
/// <see cref="IBrowserFile"/> 的串流不可 seek，而且 InputFile 一旦不再渲染（例如改選別的申請類型）就讀不到了。
/// 每張以 <c>OpenReadStream(maxAllowedSize: 10 MB)</c> 開啟，超過上限的檔不會被讀進來；
/// 張數與大小在這裡先擋一次讓顧客立刻知道，格式與最終上限仍以 <see cref="AfterSalesRequestService"/> 的檢查為準。
/// </summary>
internal static class ShopPhotoPicker
{
    public const string Accept = ".jpg,.jpeg,.png,.webp";

    /// <summary>把這次選的檔加進 <paramref name="photos"/>，回傳實際加入的張數。</summary>
    public static async Task<int> AddAsync(
        InputFileChangeEventArgs args,
        List<ShopPendingPhoto> photos,
        NotificationService notificationService,
        ILogger logger)
    {
        if (photos.Count + args.FileCount > AfterSalesRequestService.MaxFileCount)
        {
            logger.LogInformation("After-sales photos rejected by count limit. FileCount={FileCount}", photos.Count + args.FileCount);
            ViewNotification.Caution(notificationService, $"照片最多 {AfterSalesRequestService.MaxFileCount} 張，這次選的照片沒有加入。");
            return 0;
        }

        var added = 0;
        foreach (var file in args.GetMultipleFiles(AfterSalesRequestService.MaxFileCount))
        {
            if (file.Size > AfterSalesRequestService.MaxFileSize)
            {
                logger.LogInformation("After-sales photo rejected by size limit. FileSize={FileSize}", file.Size);
                ViewNotification.Caution(notificationService, $"{file.Name} 超過 10 MB，沒有加入。");
                continue;
            }

            try
            {
                await using var stream = file.OpenReadStream(maxAllowedSize: AfterSalesRequestService.MaxFileSize);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                photos.Add(new ShopPendingPhoto(Guid.NewGuid(), file.Name, file.Size, buffer.ToArray()));
                added++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to read an after-sales photo. FileSize={FileSize}", file.Size);
                ViewNotification.UnexpectedError(notificationService, $"讀取 {file.Name} 失敗，請重新選擇。", ex);
            }
        }

        return added;
    }

    /// <summary>轉成服務要的上傳檔；回傳的串流由呼叫端在送出後釋放。</summary>
    public static List<AfterSalesUploadFile> ToUploads(IEnumerable<ShopPendingPhoto> photos)
        => photos
            .Select(x => new AfterSalesUploadFile { FileName = x.Name, FileSize = x.Size, Content = new MemoryStream(x.Content, writable: false) })
            .ToList();

    public static void DisposeAll(IEnumerable<AfterSalesUploadFile> uploads)
    {
        foreach (var upload in uploads)
        {
            upload.Content.Dispose();
        }
    }

    /// <summary>給 <see cref="FormDirtyTracker"/> 的指紋：只加了照片也算有變更。</summary>
    public static string Fingerprint(IEnumerable<ShopPendingPhoto> photos) => string.Join('|', photos.Select(x => x.Id));

    public static string FormatSize(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
