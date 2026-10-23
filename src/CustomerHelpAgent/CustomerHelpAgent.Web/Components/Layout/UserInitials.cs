using System.Globalization;

namespace CustomerHelpAgent.Web.Components.Layout;

/// <summary>
/// 右上角圓形縮寫的文字（0.9.102 起，取代頭像）：姓名的第一個字；英文姓名（含空白、以英文字母開頭）取第一個與最後一個字的字首。
/// 沒有姓名時用帳號。例：「王小明」→「王」、「John Smith」→「JS」、「Mary Ann Lee」→「ML」、「alice」→「A」。
/// </summary>
internal static class UserInitials
{
    public static string From(string? name, string? account)
    {
        var text = string.IsNullOrWhiteSpace(name) ? account?.Trim() ?? string.Empty : name.Trim();
        if (text.Length == 0)
        {
            return "?";
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && char.IsAsciiLetter(words[0][0]) && char.IsAsciiLetter(words[^1][0]))
        {
            return string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[^1][0]));
        }

        // 以「文字元素」取第一個字，避免把罕用字（代理字組）切成半個字。
        var first = StringInfo.GetNextTextElement(text);
        return first.ToUpperInvariant();
    }
}
