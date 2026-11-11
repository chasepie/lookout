using System.Diagnostics.CodeAnalysis;
using Cronos;

namespace ChasePie.Lookout.Core;

/// <summary>
/// How cron expressions are read, in one place, so that validating, scheduling and previewing a cron schedule agree.
/// </summary>
public static class CronSchedules
{
    /// <summary>
    /// Reads a standard five-field cron expression, as a watch's cron schedule must be.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? expression, [NotNullWhen(true)] out CronExpression? cron)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            cron = null;
            return false;
        }

        return CronExpression.TryParse(expression, CronFormat.Standard, out cron);
    }
}
