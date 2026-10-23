using System.Reflection;
using System.Text.RegularExpressions;
using CustomerHelpAgent.Business.Helpers;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 稽核動作代碼的守門測試（LOG-14）。
///
/// 0.9.78 之前約 30 個代碼散在各呼叫點的字串字面值裡，打錯字只會在稽核頁多出一個孤兒代碼。
/// 現在代碼只能定義在 <see cref="AuditActions"/>，呼叫點一律引用常數。
/// </summary>
public sealed class AuditConventionTests
{
    /// <summary>
    /// 寫稽核的呼叫：IAuditLogService.WriteAsync，以及第一個參數是動作代碼的包裝方法
    /// （WriteAuditAsync／WriteSelfAuditAsync／WriteAiAuditAsync）。WriteExportAuditAsync 的參數是匯出格式，不在此列。
    /// </summary>
    private static readonly Regex AuditCallWithLiteral = new(
        @"(?:[Aa]uditLogService\.WriteAsync|\bWrite(?:Self|Ai)?AuditAsync)\(\s*""",
        RegexOptions.Compiled);

    [Fact]
    public void AuditCalls_ShouldNotPassStringLiteralActions()
    {
        var violations = EnumerateSourceFiles()
            .SelectMany(file => FindMatches(file, AuditCallWithLiteral))
            .ToList();

        Assert.True(violations.Count == 0,
            "稽核動作代碼必須引用 AuditActions 的常數，不可寫字串字面值："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void KnownActionCodes_ShouldOnlyBeSpelledOutInAuditActions()
    {
        // 連三元運算式（round == 0 ? "A" : "B"）或區域變數這類繞過上一條規則的寫法也一併擋下。
        // 只檢查帶「.」的代碼：單字代碼（Logout）同時也是稽核頁的分類名稱，會出現在標籤顏色對照裡。
        var codes = AllActionCodes().Where(code => code.Contains('.')).ToList();
        var violations = new List<string>();

        foreach (var file in EnumerateSourceFiles())
        {
            if (Path.GetFileName(file) == "AuditActions.cs")
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (var code in codes)
            {
                var index = text.IndexOf($"\"{code}\"", StringComparison.Ordinal);
                if (index >= 0)
                {
                    var line = text.Take(index).Count(c => c == '\n') + 1;
                    violations.Add($"{Path.GetFileName(file)}:{line} -> \"{code}\"");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "稽核動作代碼只能在 AuditActions 定義，其他地方請引用常數："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ActionCodes_ShouldBeUniqueAndWellFormed()
    {
        var codes = AllActionCodes();

        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code => Assert.Matches(@"^[A-Z][A-Za-z]*(\.[A-Z][A-Za-z]*)*$", code));
    }

    private static List<string> AllActionCodes()
        => typeof(AuditActions)
            .GetNestedTypes()
            .Append(typeof(AuditActions))
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

    private static IEnumerable<string> FindMatches(string file, Regex regex)
    {
        var text = File.ReadAllText(file);
        foreach (Match match in regex.Matches(text))
        {
            var line = text.Take(match.Index).Count(c => c == '\n') + 1;
            yield return $"{Path.GetFileName(file)}:{line}";
        }
    }

    private static IEnumerable<string> EnumerateSourceFiles()
    {
        var root = FindSourceRoot();
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}")
                        && !path.Contains("CustomerHelpAgent.Tests"));
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "CustomerHelpAgent.Web")))
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
