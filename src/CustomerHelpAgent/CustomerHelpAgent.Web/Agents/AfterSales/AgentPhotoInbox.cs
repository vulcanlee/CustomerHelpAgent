using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.Shop;

namespace CustomerHelpAgent.Web.Agents.AfterSales;

/// <summary>
/// 顧客在 Agent 對話中上傳、尚未隨申請送出的照片（Singleton，只放記憶體：重啟即清空）。
/// 張數與大小上限沿用 <see cref="AfterSalesRequestService.MaxFileCount"/>／<see cref="AfterSalesRequestService.MaxFileSize"/>；
/// 格式仍以 <see cref="AfterSalesRequestService.SubmitAsync"/> 的檢查為準。
/// ⚠️ 只有 <see cref="Take"/>／<see cref="Clear"/> 會移除，開新會話時要呼叫 <see cref="Clear"/>。
/// </summary>
public sealed class AgentPhotoInbox
{
    private readonly Dictionary<Guid, List<(string FileName, byte[] Content)>> photos = [];
    private readonly Lock gate = new();

    /// <summary>加入一張照片；已滿 <see cref="AfterSalesRequestService.MaxFileCount"/> 張或超過大小上限回 false（不加入）。</summary>
    public bool Add(Guid sessionKey, string fileName, byte[] content)
    {
        if (content.LongLength > AfterSalesRequestService.MaxFileSize)
        {
            return false;
        }

        lock (gate)
        {
            if (!photos.TryGetValue(sessionKey, out var list))
            {
                list = [];
                photos[sessionKey] = list;
            }

            if (list.Count >= AfterSalesRequestService.MaxFileCount)
            {
                return false;
            }

            list.Add((fileName, content));
            return true;
        }
    }

    /// <summary>取出並移除這個會話的全部照片（依加入順序）。</summary>
    public IReadOnlyList<AfterSalesUploadFile> Take(Guid sessionKey)
    {
        List<(string FileName, byte[] Content)>? list;
        lock (gate)
        {
            photos.Remove(sessionKey, out list);
        }

        return (list ?? [])
            .Select(x => new AfterSalesUploadFile
            {
                FileName = x.FileName,
                FileSize = x.Content.LongLength,
                Content = new MemoryStream(x.Content, writable: false),
            })
            .ToList();
    }

    public int Count(Guid sessionKey)
    {
        lock (gate)
        {
            return photos.TryGetValue(sessionKey, out var list) ? list.Count : 0;
        }
    }

    public void Clear(Guid sessionKey)
    {
        lock (gate)
        {
            photos.Remove(sessionKey);
        }
    }
}
