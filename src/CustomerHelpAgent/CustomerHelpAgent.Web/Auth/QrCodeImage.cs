using QRCoder;

namespace CustomerHelpAgent.Web.Auth;

/// <summary>
/// 產生 QR Code 的 PNG data URI（0.9.104 起，給兩步驟驗證的設定頁）。
/// ⚠️ 只用 <see cref="PngByteQRCode"/>：QRCoder 的 <c>QRCode</c> 類別依賴 System.Drawing（只支援 Windows，會觸發 CA1416 而被當成錯誤）。
/// </summary>
public static class QrCodeImage
{
    public static string ToPngDataUri(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(6);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }
}
