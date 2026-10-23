using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 忘記密碼的重設 token（0.9.60 起）。
///
/// <para>⚠️ 只存 token 的 SHA-256（十六進位），**不存原值**：資料庫外洩時拿到的雜湊無法拿來重設密碼。</para>
///
/// <para>⚠️ 刻意獨立成一張表、不在 <see cref="MyUser"/> 加欄位：管理員編輯使用者時
/// <c>MyUserService.UpdateAsync</c> 會把 AdapterModel 沒帶的 <c>MyUser</c> 欄位覆寫成預設值，
/// token 放在 <c>MyUser</c> 上會被管理員的一次存檔悄悄清掉。</para>
///
/// <para>一個帳號同時最多一筆：新申請會先刪掉舊的，重設成功後全部刪除（單次使用）。</para>
/// </summary>
public class PasswordResetToken
{
    public int Id { get; set; }

    public int MyUserId { get; set; }
    public MyUser? MyUser { get; set; }

    [Required]
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }
}
