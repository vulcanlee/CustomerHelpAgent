namespace CustomerHelpAgent.Models.Systems;

/// <summary>
/// 一封要寄出的信（與寄送方式無關）。
///
/// <para><see cref="Kind"/> 是信件種類代碼（例如 <c>Test</c>、<c>PasswordReset</c>），
/// **日誌只能記它**，不能記收件者、主旨或內文 —— 收件者是個資，內文可能夾帶重設連結。</para>
/// </summary>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody, string Kind);

/// <summary>信件種類代碼。寫進日誌與稽核的只有這個值。</summary>
public static class EmailKinds
{
    /// <summary>管理員在系統健康監控頁寄出的測試信。</summary>
    public const string Test = "Test";

    /// <summary>忘記密碼：含重設連結的信（0.9.60 起）。</summary>
    public const string PasswordReset = "PasswordReset";

    /// <summary>重設成功後通知帳號主人「密碼已變更」（0.9.60 起）。</summary>
    public const string PasswordChanged = "PasswordChanged";

    /// <summary>系統例外告警（0.9.78 起，LOG-12）。</summary>
    public const string ExceptionAlert = "ExceptionAlert";

    /// <summary>站內通知的同步信（0.9.100 起）。</summary>
    public const string Notification = "Notification";
}
