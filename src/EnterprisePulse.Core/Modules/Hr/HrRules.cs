namespace EnterprisePulse.Core.Modules.Hr;

public static class WorkingDays
{
    /// <summary>Days in [start, end] (inclusive) that are not Saturday, Sunday or a public holiday.</summary>
    public static int Count(DateOnly start, DateOnly end, IReadOnlySet<DateOnly> holidays)
    {
        var days = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (IsWeekend(d) || holidays.Contains(d)) continue;
            days++;
        }
        return days;
    }

    public static bool IsWeekend(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>Annual leave entitlement (Labour Law art. 53) by completed years of service.</summary>
    public static int AnnualLeaveDays(DateOnly hireDate, int year)
    {
        var years = year - hireDate.Year;
        return years >= 15 ? 26 : years >= 5 ? 20 : 14;
    }
}

public sealed record ShiftTimes(string? Code, string StartTime, string EndTime, int BreakMinutes, string? Color = null);

public enum DayStatus { PRESENT, LATE, ABSENT, INCOMPLETE, LEAVE, HOLIDAY, OFF, UPCOMING }

public sealed record DayResult(DayStatus Status, int PlannedMinutes, int WorkedMinutes, int LateMinutes, int OvertimeMinutes);

/// <summary>Puantaj rules: one employee day from its planned shift and check-in / check-out.</summary>
public static class Timesheet
{
    public const int DayMinutes = 24 * 60;
    /// <summary>Check-ins up to this many minutes after the shift start are not late.</summary>
    public const int LateGraceMinutes = 5;
    /// <summary>Extra time below this is not overtime (leaving a few minutes late is routine).</summary>
    public const int OvertimeThresholdMinutes = 30;

    /// <summary>Schedule of weekdays without a planned shift.</summary>
    public static readonly ShiftTimes DefaultShift = new(null, "09:00", "18:00", 60);

    public static int ParseClock(string value)
    {
        var parts = value.Split(':');
        return int.Parse(parts[0]) * 60 + int.Parse(parts[1]);
    }

    public static bool IsClock(string? value) =>
        value is { Length: 5 } && value[2] == ':' && int.TryParse(value[..2], out var h) && int.TryParse(value[3..], out var m) && h is >= 0 and < 24 && m is >= 0 and < 60;

    public static string FormatClock(int minutes)
    {
        var v = ((minutes % DayMinutes) + DayMinutes) % DayMinutes;
        return $"{v / 60:00}:{v % 60:00}";
    }

    /// <summary>Start and end in minutes since midnight of the work date; an end before the start is the next day.</summary>
    public static (int Start, int End) Window(ShiftTimes s)
    {
        var start = ParseClock(s.StartTime);
        var end = ParseClock(s.EndTime);
        if (end <= start) end += DayMinutes;
        return (start, end);
    }

    public static int PlannedMinutes(ShiftTimes s)
    {
        var (start, end) = Window(s);
        return Math.Max(0, end - start - s.BreakMinutes);
    }

    /// <summary>The break is deducted only from spans longer than twice the break.</summary>
    public static int WorkedMinutes(int checkIn, int checkOut, int breakMinutes)
    {
        var span = checkOut - checkIn;
        if (span <= 0) return 0;
        return span > breakMinutes * 2 ? span - breakMinutes : span;
    }

    /// <param name="now">Current time in minutes since local midnight of the work date (negative for future days).</param>
    public static DayResult Evaluate(ShiftTimes? shift, bool holiday, bool leave, int? checkIn, int? checkOut, int now)
    {
        var planned = shift != null && !holiday && !leave ? PlannedMinutes(shift) : 0;
        var breakMinutes = shift?.BreakMinutes ?? DefaultShift.BreakMinutes;
        var worked = checkIn != null && checkOut != null ? WorkedMinutes(checkIn.Value, checkOut.Value, breakMinutes) : 0;
        var extra = worked - planned;
        DayResult Result(DayStatus status, int late = 0) =>
            new(status, planned, worked, late, extra >= OvertimeThresholdMinutes ? extra : 0);

        if (holiday) return Result(DayStatus.HOLIDAY);
        if (leave) return Result(DayStatus.LEAVE);
        if (shift == null) return Result(DayStatus.OFF);
        var (start, end) = Window(shift);
        if (checkIn == null) return Result(now < end ? DayStatus.UPCOMING : DayStatus.ABSENT);
        var lateBy = checkIn.Value - start > LateGraceMinutes ? checkIn.Value - start : 0;
        if (checkOut == null) return Result(DayStatus.INCOMPLETE, lateBy);
        return Result(lateBy > 0 ? DayStatus.LATE : DayStatus.PRESENT, lateBy);
    }

    /// <summary>Minutes between local midnight of <paramref name="workDate"/> and the UTC instant.</summary>
    public static int MinutesSince(DateTime utc, DateOnly workDate, int offsetMinutes) =>
        (int)Math.Floor((utc - workDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).TotalMinutes) + offsetMinutes;

    public static DateTime InstantOf(DateOnly workDate, int minutes, int offsetMinutes) =>
        workDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddMinutes(minutes - offsetMinutes);
}
