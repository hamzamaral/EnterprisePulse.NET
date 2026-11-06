using EnterprisePulse.Core.Modules.Hr;

namespace EnterprisePulse.UnitTests;

public class WorkingDaysTests
{
    [Fact]
    public void Skips_weekends_and_holidays()
    {
        var holidays = new HashSet<DateOnly> { new(2026, 10, 29) };
        // Mon 26 Oct – Sun 1 Nov 2026: five weekdays, one of them Republic Day.
        Assert.Equal(4, WorkingDays.Count(new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1), holidays));
        Assert.Equal(0, WorkingDays.Count(new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 8), holidays));
        Assert.Equal(0, WorkingDays.Count(new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 2), holidays));
    }

    [Theory]
    [InlineData(2024, 14)]
    [InlineData(2021, 20)]
    [InlineData(2011, 26)]
    public void Annual_leave_follows_seniority(int hireYear, int days) =>
        Assert.Equal(days, WorkingDays.AnnualLeaveDays(new DateOnly(hireYear, 3, 1), 2026));
}

public class TimesheetTests
{
    private static readonly ShiftTimes Day = new("GND", "09:00", "18:00", 60);
    private static readonly ShiftTimes Night = new("GCE", "22:00", "06:00", 30);
    private static int At(string clock) => Timesheet.ParseClock(clock);

    [Fact]
    public void Planned_minutes_handle_overnight_shifts()
    {
        Assert.Equal(480, Timesheet.PlannedMinutes(Day));
        Assert.Equal(450, Timesheet.PlannedMinutes(Night));
        Assert.Equal((At("22:00"), At("06:00") + Timesheet.DayMinutes), Timesheet.Window(Night));
    }

    [Fact]
    public void Present_within_grace_and_late_after()
    {
        var onTime = Timesheet.Evaluate(Day, false, false, At("09:05"), At("18:00"), At("20:00"));
        Assert.Equal(DayStatus.PRESENT, onTime.Status);
        var late = Timesheet.Evaluate(Day, false, false, At("09:20"), At("18:00"), At("20:00"));
        Assert.Equal(DayStatus.LATE, late.Status);
        Assert.Equal(20, late.LateMinutes);
    }

    [Fact]
    public void Overtime_needs_threshold()
    {
        Assert.Equal(0, Timesheet.Evaluate(Day, false, false, At("09:00"), At("18:20"), At("20:00")).OvertimeMinutes);
        Assert.Equal(90, Timesheet.Evaluate(Day, false, false, At("09:00"), At("19:30"), At("20:00")).OvertimeMinutes);
    }

    [Fact]
    public void Missing_records_are_absent_upcoming_or_incomplete()
    {
        Assert.Equal(DayStatus.ABSENT, Timesheet.Evaluate(Day, false, false, null, null, At("19:00")).Status);
        Assert.Equal(DayStatus.UPCOMING, Timesheet.Evaluate(Day, false, false, null, null, At("10:00")).Status);
        Assert.Equal(DayStatus.INCOMPLETE, Timesheet.Evaluate(Day, false, false, At("09:00"), null, At("12:00")).Status);
        Assert.Equal(DayStatus.HOLIDAY, Timesheet.Evaluate(Day, true, false, null, null, At("19:00")).Status);
        Assert.Equal(DayStatus.LEAVE, Timesheet.Evaluate(Day, false, true, null, null, At("19:00")).Status);
        Assert.Equal(DayStatus.OFF, Timesheet.Evaluate(null, false, false, null, null, At("19:00")).Status);
    }

    [Fact]
    public void Break_is_deducted_only_from_long_spans()
    {
        Assert.Equal(90, Timesheet.WorkedMinutes(At("09:00"), At("10:30"), 60));
        Assert.Equal(480, Timesheet.WorkedMinutes(At("09:00"), At("18:00"), 60));
    }

    [Fact]
    public void Clock_helpers()
    {
        Assert.True(Timesheet.IsClock("23:59"));
        Assert.False(Timesheet.IsClock("24:00"));
        Assert.False(Timesheet.IsClock("9:00"));
        Assert.Equal("01:30", Timesheet.FormatClock(Timesheet.DayMinutes + 90));
        Assert.Equal(0, Timesheet.MinutesSince(new DateTime(2026, 11, 6, 21, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 11, 7), 180));
    }
}
