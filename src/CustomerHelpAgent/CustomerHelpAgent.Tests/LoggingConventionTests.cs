using System.Text.RegularExpressions;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 日誌撰寫慣例的守門測試。
///
/// 本專案的日誌是可觀測性的唯一來源，而且管理員能從 /logs 頁面**匯出原始檔案**，
/// 因此「不寫入敏感資料」與「訊息可被檢索」兩件事必須機械化保證 ——
/// 文件與 copilot-instructions 只是建議，唯有測試會擋下 PR。
///
/// 這些規則在導入當下就已全數綠燈，因此它是回歸防護而不是待辦清單。
/// </summary>
public sealed class LoggingConventionTests
{
    /// <summary>
    /// 擷取日誌呼叫的訊息樣板。可選的前綴用來吸收 LogError(ex, "...") 的例外參數。
    /// </summary>
    private static readonly Regex LogCall = new(
        @"Log(Trace|Debug|Information|Warning|Error|Critical)\s*\(\s*(?:[A-Za-z0-9_\.]+\s*,\s*)?""((?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    private static readonly Regex Placeholder = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// 絕不可作為日誌佔位名稱的字詞。比對不分大小寫的子字串。
    /// </summary>
    private static readonly string[] ForbiddenPlaceholderParts =
    [
        "password", "passwd", "pwd", "token", "secret", "salt",
        "captcha", "email", "mail", "phone", "mobile", "apikey",
        "signingkey", "clientsecret",
    ];

    /// <summary>
    /// 含敏感字詞、但實際上是旗標或計數而非機密值本身的佔位名稱。
    /// 逐一列舉而非放寬規則，讓每個例外都必須被有意識地加入。
    /// </summary>
    private static readonly string[] SafePlaceholderNames =
    [
        "NeedChangePassword",   // 布林旗標：是否需要變更密碼，不含密碼內容
    ];

    /// <summary>
    /// 絕不可作為日誌「引數」的屬性名稱。這些都是本專案真實存在的屬性，
    /// 因此精準度高：樣板可能寫 {Value}，但引數若是 user.Password 一樣是外洩。
    /// </summary>
    private static readonly Regex ForbiddenArgument = new(
        @"\.(Password|Salt|Email|SigningKey|ClientSecret|RefreshToken|AccessToken|CaptchaCode|TwoFactorSecret|SecurityStamp)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// CJK 範圍。日誌訊息一律英文，中文保留給 ApiResult.Message 與畫面通知，
    /// 這樣日後才好用關鍵字檢索。
    /// </summary>
    private static readonly Regex Cjk = new(@"[㐀-鿿豈-﫿＀-￯　-〿]", RegexOptions.Compiled);

    private static readonly Regex PascalCase = new(@"^[A-Z][A-Za-z0-9]*$", RegexOptions.Compiled);

    private static IEnumerable<(string File, string Level, string Template, string Line)> EnumerateLogCalls()
    {
        var root = FindSourceRoot();
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}")
                        && !path.Contains("CustomerHelpAgent.Tests"));

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match match in LogCall.Matches(text))
            {
                var lineNumber = text.Take(match.Index).Count(c => c == '\n') + 1;
                yield return (Path.GetFileName(file), match.Groups[1].Value, match.Groups[2].Value,
                    $"{Path.GetFileName(file)}:{lineNumber}");
            }
        }
    }

    [Fact]
    public void LogMessages_ShouldBeEnglish()
    {
        var violations = EnumerateLogCalls()
            .Where(call => Cjk.IsMatch(call.Template))
            .Select(call => $"{call.Line} -> {call.Template}")
            .ToList();

        Assert.True(violations.Count == 0,
            "日誌訊息一律英文（中文保留給 ApiResult.Message 與畫面通知）："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void LogPlaceholders_ShouldBePascalCase()
    {
        var violations = new List<string>();
        foreach (var call in EnumerateLogCalls())
        {
            foreach (Match placeholder in Placeholder.Matches(call.Template))
            {
                var name = placeholder.Groups[1].Value;
                if (!PascalCase.IsMatch(name))
                {
                    violations.Add($"{call.Line} -> {{{name}}}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "日誌佔位名稱須為 PascalCase，方便日後檢索："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void LogPlaceholders_ShouldNotNameSensitiveData()
    {
        var violations = new List<string>();
        foreach (var call in EnumerateLogCalls())
        {
            foreach (Match placeholder in Placeholder.Matches(call.Template))
            {
                var name = placeholder.Groups[1].Value;
                if (SafePlaceholderNames.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }

                var forbidden = ForbiddenPlaceholderParts
                    .FirstOrDefault(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
                if (forbidden is not null)
                {
                    violations.Add($"{call.Line} -> {{{name}}}（含「{forbidden}」）");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "日誌不得寫入敏感資料或個資。可記錄的身分資訊只有 Account 與 UserId："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void LogMessages_ShouldNotUseDestructuring()
    {
        // {@Model} 會把整個物件序列化進日誌，是最容易一次外洩全部欄位的寫法。
        var violations = EnumerateLogCalls()
            .Where(call => call.Template.Contains("{@", StringComparison.Ordinal))
            .Select(call => $"{call.Line} -> {call.Template}")
            .ToList();

        Assert.True(violations.Count == 0,
            "不得使用 {@} 解構整包物件，請逐一列出需要的欄位："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void LogArguments_ShouldNotReferenceSensitiveProperties()
    {
        var root = FindSourceRoot();
        var violations = new List<string>();

        // 樣板可能寫成無害的 {Value}，但引數若是 user.Password 一樣會外洩，
        // 因此另外掃一次「呼叫括號內的引數運算式」。
        var callWithArgs = new Regex(
            @"Log(?:Trace|Debug|Information|Warning|Error|Critical)\s*\(([^;]{0,600}?)\);",
            RegexOptions.Compiled | RegexOptions.Singleline);

        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(p => (p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}")
                     && !p.Contains("CustomerHelpAgent.Tests")))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in callWithArgs.Matches(text))
            {
                var hit = ForbiddenArgument.Match(match.Groups[1].Value);
                if (hit.Success)
                {
                    var lineNumber = text.Take(match.Index).Count(c => c == '\n') + 1;
                    violations.Add($"{Path.GetFileName(file)}:{lineNumber} -> {hit.Value}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "日誌引數不得取用敏感屬性："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 使用者輸入的查詢關鍵字可能是人名、Email、電話（搜尋「王小明」等於記錄了一個人名），
    /// 只能記「有沒有」與「多長」。0.9.77 之前各 CRUD 畫面與 API 都記了原文（LOG-08）。
    /// </summary>
    private static readonly string[] SafeSearchPlaceholderNames =
    [
        "HasSearch", "SearchLength",    // 畫面與服務層
        "HasKeyword", "KeywordLength",  // Web API 與 Repository（參數名稱是 Keyword）
    ];

    [Fact]
    public void LogPlaceholders_ShouldNotRecordSearchText()
    {
        var violations = new List<string>();
        foreach (var call in EnumerateLogCalls())
        {
            foreach (Match placeholder in Placeholder.Matches(call.Template))
            {
                var name = placeholder.Groups[1].Value;
                if (SafeSearchPlaceholderNames.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }

                if (name.Contains("search", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("keyword", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{call.Line} -> {{{name}}}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "日誌不得記錄查詢關鍵字原文，請改記 HasSearch／SearchLength（或 HasKeyword／KeywordLength）："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 本體只有空白與註解的 catch。可選的 when 篩選允許一層巢狀括號。
    /// </summary>
    private static readonly Regex EmptyCatch = new(
        @"catch\s*(?:\((?<type>[^)]*)\))?\s*(?:when\s*\((?:[^()]|\([^()]*\))*\)\s*)?\{(?:\s|//[^\n]*|/\*.*?\*/)*\}",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// 本來就該安靜忽略的例外：使用者取消、正常關機、瀏覽器已斷線、元件已釋放。
    /// </summary>
    private static readonly string[] IgnorableExceptionTypes =
    [
        "OperationCanceledException",
        "TaskCanceledException",
        "JSDisconnectedException",
        "JSException",
        "ObjectDisposedException",
    ];

    /// <summary>
    /// 允許空 catch 的檔案，逐一附理由。新增前請先確認真的不能記錄。
    /// </summary>
    private static readonly string[] EmptyCatchAllowedFiles =
    [
        // 例外紀錄管線之內：不得用 ILogger（會遞迴），且記錄失敗不得影響呼叫端。
        "ExceptionLogProvider.cs",
        "ExceptionStackFileStore.cs",
        "ExceptionLogService.cs",       // RecordAsync 由管線寫入器呼叫，刻意吞掉、不回到管線
        // 純顯示用的 JSON 美化（static），解析失敗就顯示原文，沒有任何資料受影響。
        "TokenUsageView.razor.cs",
    ];

    [Fact]
    public void CatchBlocks_ShouldNotBeEmpty()
    {
        var root = FindSourceRoot();
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(p => (p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}")
                     && !p.Contains("CustomerHelpAgent.Tests")))
        {
            var name = Path.GetFileName(file);
            if (EmptyCatchAllowedFiles.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match match in EmptyCatch.Matches(text))
            {
                // 「catch (Foo.BarException ex)」→「BarException」
                var type = match.Groups["type"].Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.Split('.').Last() ?? string.Empty;
                if (IgnorableExceptionTypes.Contains(type, StringComparer.Ordinal))
                {
                    continue;
                }

                var lineNumber = text.Take(match.Index).Count(c => c == '\n') + 1;
                violations.Add($"{name}:{lineNumber} -> catch ({(type.Length == 0 ? "（全部）" : type)})");
            }
        }

        Assert.True(violations.Count == 0,
            "catch 區塊不得是空的或只有註解：至少記一筆 Debug／Warning 並說明為什麼可以忽略"
                + "（例外紀錄管線內改用 NLog InternalLogger）："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 會出問題的地方必須有 logger。下一個新增的服務或檢視才不會又忘記寫日誌。
    /// </summary>
    [Theory]
    [InlineData("CustomerHelpAgent.Business", "Services")]
    [InlineData("CustomerHelpAgent.Business", "Repositories")]
    [InlineData("CustomerHelpAgent.Business", "Startup")]
    [InlineData("CustomerHelpAgent.Web", "Controllers")]
    [InlineData("CustomerHelpAgent.Web", "Diagnostics")]
    [InlineData("CustomerHelpAgent.Web", "Auth")]
    [InlineData("CustomerHelpAgent.Web", "Filters")]
    [InlineData("CustomerHelpAgent.Web", "Health")]
    [InlineData("CustomerHelpAgent.Web", "Scheduling")]
    public void BehaviourClasses_ShouldHoldALogger(string project, string folder)
    {
        var root = Path.Combine(FindSourceRoot(), project, folder);
        if (!Directory.Exists(root))
        {
            return;
        }

        var violations = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ExemptFromLoggerRequirement(path))
            .Where(path => !File.ReadAllText(path).Contains("ILogger<", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(violations.Count == 0,
            $"{project}/{folder} 下的類別必須注入 ILogger（純資料/純函式類別請加入豁免清單）："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 豁免：介面、設定與模型等純宣告檔案，以及沒有行為的狀態持有者與純函式類別。
    /// 硬要它們注入 logger 只會產生永遠不會被寫出的雜訊。
    /// </summary>
    private static bool ExemptFromLoggerRequirement(string path)
    {
        var name = Path.GetFileName(path);

        if (name.StartsWith('I') && name.Length > 1 && char.IsUpper(name[1]))
        {
            return true;
        }

        string[] exempt =
        [
            "CurrentUserService.cs",        // 僅持有目前使用者狀態，無任何行為
            "RolePermissionService.cs",     // 純粹回傳靜態權限結構
            "AuthenticationCheckResult.cs", // 列舉
            "PasswordResetRequestResult.cs", // 列舉
            "LogModels.cs",                 // 模型與純轉換
            "DatabaseUsageModels.cs",       // 模型
            "NLogFilePathResolver.cs",      // 純路徑組字串
            "TokenUsageFormat.cs",          // 純數字格式化（K／M 精簡顯示），無任何行為
            "AiUsageCostCalculator.cs",     // 純算術：讀設定、套費率、回金額，沒有 I/O 也沒有可失敗的副作用
            "TotpService.cs",               // 純密碼學運算；所有輸入輸出都是機密，加 logger 只會誘使人記錄它
            "DatabaseInitializerOptions.cs", // 設定
            "DatabaseInitializationLock.cs", // 由 DatabaseInitializer 傳入 logger（靜態輔助類別無法注入）
            "DemoSeedData.cs",              // 售後展示的示範資料定義（靜態、無 I/O）；寫入與日誌在兩個 Demo*Seeder

            // ⚠️ 以下四支位在「系統例外紀錄」的記錄管線之內，**刻意不得注入 ILogger**。
            // 它們一旦用 ILogger 記錄自己的失敗，那筆記錄會再被管線收進來、再嘗試寫入、再失敗 ——
            // 形成「失敗 → 記錄 → 再失敗」的無限遞迴。需要留話時一律走 NLog 的 InternalLogger。
            // 這不是漏加 logger，請勿「順手補上」。
            "ExceptionContextAccessor.cs",  // AsyncLocal 狀態持有者，無行為
            "ExceptionLogProvider.cs",      // 記錄管線的進入點；用 ILogger 會遞迴
            "ExceptionLogWriter.cs",        // 記錄管線的出口；用 ILogger 會遞迴
            "ExceptionStackFileStore.cs",   // 由管線內呼叫的檔案存取；用 ILogger 會遞迴
            "CrashMarkerStore.cs",          // 程序即將結束或 host 尚未建立時寫補登檔，記錄機制可能已失效
            "TraceCode.cs",                 // 純函式：產生與讀取錯誤追蹤碼（LOG-10）
            "ExceptionAlertService.cs",     // 在例外記錄管線內被呼叫；寄信失敗走 ILogger 會再被收成例外、再觸發告警
            "LoggingPipelineMonitor.cs",    // 由 NLog 內部事件與例外寫入器呼叫；在 NLog 內部事件裡用 logger 會死結或遞迴
            "BrowserScriptException.cs",    // 例外型別，無行為
            "RequestActorResolver.cs",      // 純函式：從 claims 取帳號與 UserId
            "SystemHealthModels.cs",        // 健康監控的模型
            "SystemHealthScoreCalculator.cs", // 純算術：由各項狀態算分數與燈號
            "SystemStartupState.cs",        // 只持有啟動時間，無行為
            "ScheduledJobDescriptor.cs",    // 排程作業的描述（record），無行為
            "ScheduleCalculator.cs",        // 純函式：cron 的下次時段與補跑時段
            "ScheduledJobTriggerQueue.cs",  // 「立即執行」請求的佇列與去重，無 I/O
            "NotificationCategories.cs",    // 通知分類常數，無行為（0.9.100 起）
            "PasswordPolicy.cs",            // 密碼原則：純規則計算與雜湊，失敗由呼叫端記錄（0.9.101 起）
            "CookieClaims.cs",              // 登入 Cookie 的 claims 組裝，純函式（0.9.103 起）
            "ClientIpProvider.cs",          // 稽核來源 IP：持有 circuit 的 IP 並格式化，無 I/O；失敗由 circuit handler 記錄（0.9.117 起）
            "QrCodeImage.cs",               // 文字轉 QR Code 圖片，純函式（0.9.104 起）
            "SystemIdentity.cs",            // 例外告警服務（例外記錄管線內）使用它讀系統名稱；用 ILogger 會遞迴（0.9.98 起）
            "UpdateOutcome.cs",             // Repository 修改結果（record：是否成功＋稽核差異），無行為（0.9.131 起）
            "RetentionStats.cs",            // 資料保留頁的統計（record：筆數、最舊、過期），無行為（0.9.133 起）
            "RetentionCutoffs.cs",          // 純函式：各類資料超過保留天數的門檻（0.9.133 起）
            "CronDescriber.cs",             // 純函式：cron 轉白話（0.9.133 起）
        ];

        if (name.EndsWith("Extensions.cs", StringComparison.Ordinal))
        {
            // 靜態擴充方法類別，沒有可注入的建構式
            return true;
        }

        return exempt.Contains(name, StringComparer.Ordinal)
            || name.EndsWith("Settings.cs", StringComparison.Ordinal)
            || name.EndsWith("Dto.cs", StringComparison.Ordinal);
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "CustomerHelpAgent.Web");
            if (Directory.Exists(candidate))
            {
                return dir.FullName;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "CustomerHelpAgent");
            if (Directory.Exists(Path.Combine(srcCandidate, "CustomerHelpAgent.Web")))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 src/CustomerHelpAgent。");
    }
}
