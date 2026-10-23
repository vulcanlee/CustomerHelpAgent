using System.Globalization;

namespace CustomerHelpAgent.Business.Helpers;

/// <summary>
/// 稽核用的修改前後差異（0.9.131 起）：產生「欄位：舊值 → 新值」文字，寫進稽核的詳細內容檔
/// （<c>IAuditLogService.WriteAsync(content: …)</c>），不進資料庫、不進日誌。
///
/// 用法：<c>AuditDiff.Compare(before, after).Masked("Password", …).Set("Teams", 舊, 新).Render("分類 #12")</c>；
/// 沒有任何差異時 <see cref="Render"/> 回 null，呼叫端就不寫檔。
///
/// ⚠️ 只比對<b>純量屬性</b>（字串、數值、布林、列舉、時間）。下列一律不比：主鍵、版本號、建立／更新時間、
/// 軟刪除欄位（由系統維護，不是使用者改的）、導覽屬性與集合（關聯請用 <see cref="Set"/> 自己列）。
/// 密碼這類欄位必須放進 <c>ignore</c> 再用 <see cref="Masked"/> 只記「已變更」，絕不可讓值出現在內容檔裡。
/// </summary>
public sealed class AuditDiff
{
    /// <summary>空值在內容檔裡的寫法。</summary>
    public const string Empty = "（空）";

    private static readonly HashSet<string> SystemFields = new(StringComparer.Ordinal)
    {
        "Id", "ConcurrencyStamp",
        "CreatedAt", "UpdatedAt", "CreateAt", "UpdateAt",
        "IsDeleted", "DeletedAt", "DeletedBy",
    };

    private readonly List<(string Field, string Text)> changes = [];

    /// <summary>有差異的欄位名稱（依加入順序）。</summary>
    public IReadOnlyList<string> ChangedFields => changes.Select(x => x.Field).ToList();

    public bool HasChanges => changes.Count > 0;

    /// <summary>比對同型別兩個物件的純量屬性；<paramref name="ignore"/> 列出不比對的欄位（例如密碼、系統維護欄位）。</summary>
    public static AuditDiff Compare<T>(T before, T after, params string[] ignore)
        where T : class
    {
        var diff = new AuditDiff();
        var skipped = new HashSet<string>(ignore, StringComparer.Ordinal);

        foreach (var property in typeof(T).GetProperties())
        {
            if (property.CanRead == false
                || property.GetIndexParameters().Length > 0
                || SystemFields.Contains(property.Name)
                || skipped.Contains(property.Name)
                || IsScalar(property.PropertyType) == false)
            {
                continue;
            }

            diff.Value(property.Name, property.GetValue(before), property.GetValue(after));
        }

        return diff;
    }

    /// <summary>值不同時記一筆（空字串與 null 視為相同）。</summary>
    public AuditDiff Value(string field, object? before, object? after)
    {
        var oldText = Format(before);
        var newText = Format(after);
        if (oldText != newText)
        {
            changes.Add((field, Describe(field, oldText, newText)));
        }

        return this;
    }

    /// <summary>只記「已變更」、不記值（密碼、金鑰這類欄位）。<paramref name="changed"/> 為 false 時不記。</summary>
    public AuditDiff Masked(string field, bool changed)
    {
        if (changed)
        {
            changes.Add((field, $"{field}: 已變更（內容不記錄）"));
        }

        return this;
    }

    /// <summary>集合欄位（角色、團隊、權限）：只列新增與移除的項目。</summary>
    public AuditDiff Set(string field, IEnumerable<string> before, IEnumerable<string> after)
    {
        var oldSet = before.Where(x => string.IsNullOrWhiteSpace(x) == false).ToHashSet(StringComparer.Ordinal);
        var newSet = after.Where(x => string.IsNullOrWhiteSpace(x) == false).ToHashSet(StringComparer.Ordinal);
        var added = newSet.Except(oldSet).Order(StringComparer.Ordinal).ToList();
        var removed = oldSet.Except(newSet).Order(StringComparer.Ordinal).ToList();
        if (added.Count == 0 && removed.Count == 0)
        {
            return this;
        }

        var parts = new List<string>();
        if (added.Count > 0)
        {
            parts.Add($"新增 [{string.Join(", ", added)}]");
        }

        if (removed.Count > 0)
        {
            parts.Add($"移除 [{string.Join(", ", removed)}]");
        }

        changes.Add((field, $"{field}: {string.Join("；", parts)}"));
        return this;
    }

    /// <summary>內容檔全文；沒有差異時回 null（呼叫端不寫檔）。</summary>
    public string? Render(string title)
    {
        if (changes.Count == 0)
        {
            return null;
        }

        // 一律用 \n：內容檔在不同作業系統之間搬動（備份還原）時，換行不會混雜。
        return string.Join("\n", changes.Select(x => x.Text).Prepend(title));
    }

    private static string Describe(string field, string oldText, string newText)
    {
        // 多行內容（說明、公告內文）用區塊呈現，否則「舊 → 新」擠在一行看不出改了哪裡。
        if (oldText.Contains('\n') || newText.Contains('\n'))
        {
            return string.Join("\n",
                $"{field}:",
                "----- 修改前 -----",
                oldText,
                "----- 修改後 -----",
                newText,
                "-----");
        }

        return $"{field}: {oldText} → {newText}";
    }

    private static string Format(object? value) => value switch
    {
        null => Empty,
        string text when string.IsNullOrEmpty(text) => Empty,
        string text => text.Replace("\r\n", "\n"),
        DateTime time => time.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? Empty,
    };

    private static bool IsScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive
            || underlying.IsEnum
            || underlying == typeof(string)
            || underlying == typeof(decimal)
            || underlying == typeof(DateTime)
            || underlying == typeof(DateTimeOffset)
            || underlying == typeof(Guid);
    }
}
