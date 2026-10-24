using System.Text.Json;
using Microsoft.Extensions.AI;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Web.Agents.PreReview;

/// <summary>照片初審的結構化輸出（也是 <see cref="PreReviewFinding.Json"/> 的明細）。</summary>
public sealed record PhotoReviewResult(string Verdict, IReadOnlyList<string> Observations, string Confidence)
{
    public const string Consistent = "consistent";
    public const string Inconsistent = "inconsistent";
    public const string Unclear = "unclear";

    public static readonly IReadOnlyList<string> Verdicts = [Consistent, Inconsistent, Unclear];
    public static readonly IReadOnlyList<string> Confidences = ["low", "medium", "high"];
}

/// <summary>
/// 照片初審（模型節點，看圖）：送出申請的照片（<see cref="DataContent"/>，最多 <see cref="AfterSalesRequestService.MaxFileCount"/> 張）
/// ＋文字（只有申請類型與顧客描述，描述經 <see cref="PreReviewPrivacy"/> 遮蔽個資），以結構化輸出取得 <see cref="PhotoReviewResult"/>。
/// ⚠️ 照片只送給這個節點。沒有附照片不呼叫模型；個別照片檔讀不到就略過那一張（記 Warning），
/// 一張都讀不到時拋例外（節點改為 Failed，請客服自行檢視）。
/// </summary>
public sealed class ModelPhotoReviewer : IPhotoReviewer
{
    private readonly PreReviewChatClientProvider models;
    private readonly AfterSalesRequestService requests;
    private readonly ILogger<ModelPhotoReviewer> logger;

    public ModelPhotoReviewer(PreReviewChatClientProvider models, AfterSalesRequestService requests, ILogger<ModelPhotoReviewer> logger)
    {
        this.models = models;
        this.requests = requests;
        this.logger = logger;
    }

    public async Task<PreReviewFinding> ReviewAsync(PreReviewCase reviewCase, CancellationToken cancellationToken)
    {
        if (reviewCase.AttachmentIds.Count == 0)
        {
            return new PreReviewFinding(PreReviewNode.PhotoReview, FindingStatus.NotApplicable, "未附照片，略過照片初審", "{}");
        }

        var text = await PreReviewPrivacy.RedactAsync(requests, reviewCase.RequestId, $"""
            售後類型：{AfterSalesLabels.TypeText(reviewCase.Type)}
            顧客描述：{reviewCase.Description}
            """);
        List<AIContent> contents = [new TextContent(text)];
        foreach (var attachmentId in reviewCase.AttachmentIds.Take(AfterSalesRequestService.MaxFileCount))
        {
            // 背景執行（系統身分）以審核者權限讀附件；路徑由 AfterSalesFileStore.TryResolveFullPath 限定在設定的根目錄內。
            // 讀不到的那一張略過（只記附件 Id），其他照片照樣送；一張都讀不到才讓節點失敗。
            if (await requests.GetAttachmentForAsync(attachmentId, userId: 0, canReview: true) is { } file)
            {
                contents.Add(new DataContent(await File.ReadAllBytesAsync(file.FullPath, cancellationToken), file.ContentType));
            }
            else
            {
                logger.LogWarning("Photo pre-review skipped an unreadable attachment. RunId={RunId}, AttachmentId={AttachmentId}", reviewCase.RunId, attachmentId);
            }
        }

        var images = contents.Count - 1;
        if (images == 0)
        {
            throw new InvalidOperationException("None of the after-sales attachments could be read.");
        }

        logger.LogInformation("Photo pre-review sends images to the model. RunId={RunId}, ImageCount={ImageCount}", reviewCase.RunId, images);
        var result = Normalize(await models.RunStructuredAsync<PhotoReviewResult>(
            reviewCase,
            AgentRecordingStreams.PreReviewPhoto,
            vision: true,
            PromptTemplateKeys.AfterSalesPreReviewPhoto,
            new ChatMessage(ChatRole.User, contents),
            cancellationToken));
        return new PreReviewFinding(
            PreReviewNode.PhotoReview,
            FindingStatus.Ok,
            Headline(result),
            JsonSerializer.Serialize(result, PreReviewJsonContext.Default.PhotoReviewResult));
    }

    /// <summary>值不在允許清單內時改為最保守的值（無法判斷、低信心）；清單為 null 時給空清單。</summary>
    private static PhotoReviewResult Normalize(PhotoReviewResult result) => new(
        PhotoReviewResult.Verdicts.Contains(result.Verdict) ? result.Verdict : PhotoReviewResult.Unclear,
        result.Observations ?? [],
        PhotoReviewResult.Confidences.Contains(result.Confidence) ? result.Confidence : "low");

    private static string Headline(PhotoReviewResult result)
    {
        var verdict = result.Verdict switch
        {
            PhotoReviewResult.Consistent => "照片與描述一致",
            PhotoReviewResult.Inconsistent => "照片與描述不一致",
            _ => "照片無法判斷",
        };
        var confidence = result.Confidence switch
        {
            "high" => "高",
            "medium" => "中",
            _ => "低",
        };
        return $"{verdict}（信心：{confidence}）";
    }
}
