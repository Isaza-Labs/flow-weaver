using Cronos;

namespace flow_weaver_backend.Services.Scheduler;

// Wraps Cronos to turn a trigger's (CronExpression, Timezone) into the next
// UTC fire time. Supports BOTH standard 5-field cron (minute granularity)
// and 6-field cron with a leading seconds column, so sub-minute cadence like
// "*/30 * * * * *" (every 30 seconds) is expressible — this is option B of
// the schedule refactor. Everything here is static + allocation-light so the
// hosted service can call it every tick.
public static class CronSchedule
{
    // Parses `expression` (5 or 6 whitespace-separated fields). Returns null
    // with `error` populated when the field count is wrong or Cronos rejects
    // the syntax, so callers can surface a precise 400.
    public static CronExpression? TryParse(string? expression, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "cron expression is empty";
            return null;
        }

        var fieldCount = expression.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var format = fieldCount switch
        {
            5 => CronFormat.Standard,
            6 => CronFormat.IncludeSeconds,
            _ => (CronFormat?)null,
        };
        if (format is null)
        {
            error = "cron expression must have 5 fields (minute granularity, e.g. \"*/5 * * * *\") "
                + "or 6 fields with a leading seconds column (e.g. \"*/30 * * * * *\" = every 30s); "
                + $"got {fieldCount} field(s)";
            return null;
        }

        try
        {
            return CronExpression.Parse(expression, format.Value);
        }
        catch (CronFormatException ex)
        {
            error = $"invalid cron expression: {ex.Message}";
            return null;
        }
    }

    // Resolves the trigger's IANA timezone, defaulting to UTC when empty or
    // unknown so a bad tz string never silently kills a schedule.
    public static TimeZoneInfo ResolveTimeZone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(timezone); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    // Next UTC occurrence strictly after `fromUtc`. Returns null (with `error`
    // set) when the expression can't be parsed, or null (error == null) when a
    // valid expression simply has no future occurrence.
    public static DateTime? ComputeNextUtc(
        string? expression, string? timezone, DateTime fromUtc, out string? error)
    {
        var cron = TryParse(expression, out error);
        if (cron is null) return null;

        var zone = ResolveTimeZone(timezone);
        var from = fromUtc.Kind == DateTimeKind.Utc
            ? fromUtc
            : DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        return cron.GetNextOccurrence(from, zone, inclusive: false);
    }
}
