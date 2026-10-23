using System.Text;

namespace CustomerHelpAgent.Web.Components.Commons;

/// <summary>
/// 下載用的文字位元組（一律含 UTF-8 BOM）。
///
/// 這段原本在三個匯出點各寫一份（日誌檢視的 <c>.log</c>、系統例外紀錄的 CSV、
/// Token 用量的 CSV），而且**其中兩份是壞的** —— 三份都寫成
/// <c>new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(text)</c>，
/// 註解也都寫著「加 BOM，否則 Excel 開啟繁體中文會亂碼」。
///
/// ⚠️ <b><c>GetBytes</c> 不會輸出前導碼。</b>那個旗標只影響 <c>GetPreamble()</c> 與
/// <c>StreamWriter</c>，所以旗標設了也一樣沒有 BOM，必須自己把 <c>GetPreamble()</c>
/// 接在前面。
///
/// 這個 bug 的可怕之處在於**建置、測試、程式碼審閱全都看不出來** ——
/// 程式碼看起來完全正確，要實際下載一次、抓出前三個位元組才會發現。
/// 收斂到這裡之後只有一個修改點，並由
/// <c>CustomerHelpAgent.Tests/ExportEncodingConventionTests.cs</c> 守住：
/// 本檔以外的 Web 原始碼不得再出現 <c>encoderShouldEmitUTF8Identifier</c>。
/// </summary>
public static class TextDownloadPayload
{
    /// <summary>
    /// 把文字轉成含 UTF-8 BOM 的位元組，供 <c>appFileDownload.downloadFromStream</c> 下載。
    /// </summary>
    /// <remarks>
    /// 空字串也會回傳那三個 BOM 位元組（查無資料時仍下載得到一個編碼正確的空檔）。
    /// </remarks>
    public static byte[] Utf8WithBom(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(text);

        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }
}
