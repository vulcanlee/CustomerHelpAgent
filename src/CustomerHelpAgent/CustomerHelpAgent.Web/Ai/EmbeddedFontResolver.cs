using System.Collections.Concurrent;
using PdfSharp.Fonts;

namespace CustomerHelpAgent.Web.Ai;

/// <summary>
/// PDF 報告的中文字型解析器。字型以 <c>EmbeddedResource</c> 內嵌在本組件內。
///
/// <para>
/// ⚠️ 刻意<b>不</b>讀取作業系統字型：未來若部署到 Linux 或 Docker，容器通常沒有任何
/// 中文字型，PDFsharp 會靜默掉字（PDF 出來整片空白方框），而那種問題在 Windows
/// 本機永遠測不出來。
/// </para>
/// <para>
/// 只有 Regular 一個字重。PDFsharp 只實作了斜體模擬、<b>沒有</b>粗體模擬，所以
/// <see cref="AiReportPdfBuilder"/> 一律不設 <c>Font.Bold</c>，層級改用字級、顏色與
/// 框線表達。字型來源與「為何是這個格式」見 <c>Fonts/README.md</c>。
/// </para>
/// </summary>
public sealed class EmbeddedFontResolver : IFontResolver
{
    /// <summary>MigraDoc 的樣式統一指定這個家族名稱。</summary>
    public const string FamilyName = "Noto Sans TC";

    /// <summary>
    /// 字面識別字串。這是我們自己定義的，PDFsharp 只會把
    /// <see cref="ResolveTypeface"/> 的回傳值原樣交回給 <see cref="GetFont"/>，
    /// 所以格式不拘。
    /// </summary>
    private const string RegularFaceName = "NotoSansTC#Regular";

    private const string RegularResourceName = "CustomerHelpAgent.Web.Fonts.NotoSansTC-Regular.ttf";

    private static readonly object RegistrationGate = new();
    private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.Ordinal);
    private static bool registered;

    public static EmbeddedFontResolver Instance { get; } = new();

    /// <summary>
    /// 註冊全域字型解析器。
    ///
    /// <para>
    /// ⚠️ <c>GlobalFontSettings.FontResolver</c> 是 process 全域 static 且 write-once
    /// （PDFsharp 規定第一個 XFont 建立之後就不得再設定）。因此這裡做成冪等，並且要從
    /// 兩處呼叫：
    /// </para>
    /// <list type="number">
    /// <item><c>Program.cs</c> 啟動時（正式路徑）。</item>
    /// <item><see cref="AiReportPdfBuilder"/> 的進入點（測試不會跑 <c>Program.Main</c>，
    /// 而 xUnit 預設會平行執行不同的 test collection）。</item>
    /// </list>
    /// <para>
    /// 如果改成直接指派，整合測試用 <c>WebApplicationFactory</c> 在同一個 process 內
    /// 第二次啟動 host 時就會丟例外，拖垮整組測試。
    /// </para>
    /// </summary>
    public static void EnsureRegistered()
    {
        if (registered)
        {
            return;
        }

        lock (RegistrationGate)
        {
            if (registered)
            {
                return;
            }

            GlobalFontSettings.FontResolver ??= Instance;
            registered = true;
        }
    }

    /// <summary>
    /// 內嵌字型資源是否存在。缺檔時 PDF 匯出要以中文訊息拒絕，而不是產出滿版方框的檔案。
    /// </summary>
    public static bool IsFontAvailable() => TryLoad(RegularResourceName, out _);

    /// <summary>內嵌字型資源的位元組長度。0 代表資源不存在。供守門測試斷言。</summary>
    public static int GetFontByteCount() => TryLoad(RegularResourceName, out var bytes) ? bytes.Length : 0;

    /// <summary>
    /// 解析字面。刻意<b>不</b>比對 <paramref name="familyName"/>：整份 MigraDoc 文件都用
    /// 同一個家族，任何漏設字型的樣式也會導到這裡，避免「某一段悄悄掉字」。
    /// 沒有斜體與粗體字面，兩個旗標都忽略（也不設 <c>mustSimulateBold</c>，PDFsharp 沒實作）。
    /// </summary>
    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        => new FontResolverInfo(RegularFaceName);

    public byte[]? GetFont(string faceName) => TryLoad(RegularResourceName, out var bytes) ? bytes : null;

    private static bool TryLoad(string resourceName, out byte[] bytes)
    {
        if (Cache.TryGetValue(resourceName, out var cached))
        {
            bytes = cached;
            return true;
        }

        using var stream = typeof(EmbeddedFontResolver).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            bytes = [];
            return false;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        bytes = Cache.GetOrAdd(resourceName, buffer.ToArray());
        return true;
    }
}
