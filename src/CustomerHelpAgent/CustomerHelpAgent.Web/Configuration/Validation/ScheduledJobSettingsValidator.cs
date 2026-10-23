using Microsoft.Extensions.Options;
using CustomerHelpAgent.Web.Scheduling;

namespace CustomerHelpAgent.Web.Configuration.Validation;

/// <summary>
/// <c>ScheduledJobSettings</c>（0.9.96 起）：作業名稱必須是已註冊的作業（拼錯的話覆寫會靜默失效）、
/// cron 必須是 5 欄位標準格式而且**真的會觸發**（<c>0 0 30 2 *</c> 解析得過但永遠不會到）、保留天數在範圍內。
/// </summary>
public sealed class ScheduledJobSettingsValidator : IValidateOptions<ScheduledJobSettings>
{
    private readonly HashSet<string> knownJobNames;

    public ScheduledJobSettingsValidator(IEnumerable<ScheduledJobDescriptor> descriptors)
    {
        knownJobNames = descriptors.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public ValidateOptionsResult Validate(string? name, ScheduledJobSettings options)
    {
        var errors = new OptionsErrors();
        errors.RequireRange($"{ScheduledJobSettings.SectionName}:{nameof(options.JobRunRetentionDays)}", options.JobRunRetentionDays, 0, 36500);

        foreach (var (jobName, jobOverride) in options.Jobs)
        {
            var key = $"{ScheduledJobSettings.SectionName}:{nameof(options.Jobs)}:{jobName}";
            if (!knownJobNames.Contains(jobName))
            {
                errors.Add(key, $"不是已註冊的排程作業。可用的名稱：{string.Join("、", knownJobNames.Order(StringComparer.Ordinal))}。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(jobOverride.Cron))
            {
                continue;
            }

            if (!ScheduleCalculator.TryParse(jobOverride.Cron, out var cron))
            {
                errors.Add($"{key}:Cron", $"不是合法的 5 欄位 cron（分 時 日 月 星期），目前是「{jobOverride.Cron}」。例：0 3 * * * 是每天 03:00。");
            }
            else if (ScheduleCalculator.NextOccurrenceUtc(cron!, DateTime.UtcNow, TimeZoneInfo.Local) is null)
            {
                errors.Add($"{key}:Cron", $"永遠不會觸發，目前是「{jobOverride.Cron}」。請確認日期存在（例如 2 月沒有 30 日）。");
            }
        }

        return errors.ToResult();
    }
}
