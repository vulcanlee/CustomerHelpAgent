using System.Linq.Expressions;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Business.Helpers;

/// <summary>
/// 紀錄的團隊可見性與指派規則（0.9.105 起集中在這裡）：Blazor 的服務與 Web API 的 Repository 共用同一套判斷，
/// 兩條路徑看到的資料才會一致。管理員一律放行；<see cref="RecordAccessScope.Denied"/> 一律擋下。
/// </summary>
public static class RecordTeamScope
{
    public const string DeniedAssignmentMessage = "無法確認你的身分，不能指定團隊。";
    public const string ClearToPublicMessage = "這筆資料原本有指定團隊，不能改成不指定（公開）。";

    /// <summary>專案：沒有團隊（公開）或與授權團隊有交集才看得到。</summary>
    public static IQueryable<T> Apply<T>(IQueryable<T> source, Expression<Func<T, string?>> teams, RecordAccessScope scope)
    {
        if (scope.Denied)
        {
            return source.Where(_ => false);
        }

        return scope.IsAdmin ? source : source.Where(TagStringHelper.BuildTeamAccessPredicate(teams, scope.Teams));
    }

    public static bool CanAccess(string? storedTeams, RecordAccessScope scope)
        => !scope.Denied && TagStringHelper.IsTeamAccessible(storedTeams, scope.Teams, scope.IsAdmin);

    /// <summary>分類：反向規則 —— 使用者沒有任何團隊時看得到全部分類；有團隊時只看得到公開的與自己團隊的。</summary>
    public static IQueryable<Category> ApplyCategory(IQueryable<Category> source, RecordAccessScope scope)
    {
        if (scope.Denied)
        {
            return source.Where(_ => false);
        }

        return scope.IsAdmin || scope.Teams.Count == 0
            ? source
            : source.Where(TagStringHelper.BuildTeamAccessPredicate<Category>(x => x.Teams, scope.Teams));
    }

    public static bool CanAccessCategory(string? storedTeams, RecordAccessScope scope)
        => !scope.Denied && (scope.Teams.Count == 0 || TagStringHelper.IsTeamAccessible(storedTeams, scope.Teams, scope.IsAdmin));

    /// <summary>
    /// 非管理員指定團隊：新加上的團隊必須都在自己的範圍內（原本就有的可以保留或拿掉），原本有團隊的不可清成公開。
    /// 新增時 <paramref name="currentStored"/> 傳 null。回傳錯誤訊息，沒問題回 null。
    /// </summary>
    public static string? CheckAssignment(string? currentStored, string? requestedStored, RecordAccessScope scope)
    {
        if (scope.IsAdmin)
        {
            return null;
        }

        if (scope.Denied)
        {
            return DeniedAssignmentMessage;
        }

        var current = TagStringHelper.ToList(currentStored);
        var requested = TagStringHelper.ToList(requestedStored);
        var outside = requested
            .Where(x => !current.Any(c => Same(c, x)) && !scope.Teams.Any(t => Same(t, x)))
            .ToList();
        if (outside.Count > 0)
        {
            return $"只能指定你所屬範圍內的團隊（「{string.Join("」「", outside)}」不在你的範圍內）。";
        }

        return current.Count > 0 && requested.Count == 0 ? ClearToPublicMessage : null;
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
