using flow_weaver_backend.Services.Scheduler;

namespace flow_weaver_backend.Tests;

// Locks in the schedule cadence math the SchedulerHostedService relies on —
// especially 6-field (seconds) cron, which is what makes "every 30 seconds"
// expressible (option B of the schedule refactor). ComputeNextUtc takes the
// `from` instant as a parameter, so these are fully deterministic.
public class CronScheduleTests
{
    private static readonly DateTime Base =
        new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Six_field_every_30s_advances_to_the_next_half_minute()
    {
        var from = Base.AddSeconds(10); // 00:00:10
        var next = CronSchedule.ComputeNextUtc("*/30 * * * * *", "UTC", from, out var error);

        Assert.Null(error);
        Assert.Equal(Base.AddSeconds(30), next); // 00:00:30
    }

    [Fact]
    public void Six_field_every_30s_at_boundary_is_strictly_after()
    {
        var next = CronSchedule.ComputeNextUtc("*/30 * * * * *", "UTC", Base, out _);
        // inclusive:false — 00:00:00 itself is skipped, next is 00:00:30.
        Assert.Equal(Base.AddSeconds(30), next);
    }

    [Fact]
    public void Five_field_every_5_minutes_parses_and_advances()
    {
        var from = Base.AddMinutes(2); // 00:02
        var next = CronSchedule.ComputeNextUtc("*/5 * * * *", "UTC", from, out var error);

        Assert.Null(error);
        Assert.Equal(Base.AddMinutes(5), next); // 00:05
    }

    [Fact]
    public void Six_field_every_2_hours_advances()
    {
        var from = Base.AddMinutes(30); // 00:30
        var next = CronSchedule.ComputeNextUtc("0 0 */2 * * *", "UTC", from, out _);
        Assert.Equal(Base.AddHours(2), next); // 02:00:00
    }

    [Theory]
    [InlineData("* * * *")]        // 4 fields
    [InlineData("* * * * * * *")]  // 7 fields
    public void Wrong_field_count_is_rejected(string expr)
    {
        var result = CronSchedule.TryParse(expr, out var error);
        Assert.Null(result);
        Assert.NotNull(error);
    }

    [Fact]
    public void Garbage_expression_is_rejected_with_error()
    {
        var next = CronSchedule.ComputeNextUtc("not a cron", "UTC", Base, out var error);
        Assert.Null(next);
        Assert.NotNull(error);
    }

    [Fact]
    public void Blank_expression_is_rejected()
    {
        var result = CronSchedule.TryParse("   ", out var error);
        Assert.Null(result);
        Assert.NotNull(error);
    }

    [Fact]
    public void Unknown_timezone_falls_back_to_utc()
    {
        // A bad tz must never throw or silently stop a schedule — it resolves
        // to UTC so the trigger still fires.
        var zone = CronSchedule.ResolveTimeZone("Mars/Olympus_Mons");
        Assert.Equal(TimeZoneInfo.Utc, zone);
    }

    [Fact]
    public void Empty_timezone_resolves_to_utc()
    {
        Assert.Equal(TimeZoneInfo.Utc, CronSchedule.ResolveTimeZone(""));
        Assert.Equal(TimeZoneInfo.Utc, CronSchedule.ResolveTimeZone(null));
    }
}
