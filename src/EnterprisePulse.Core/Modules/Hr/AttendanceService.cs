using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Hr;

public sealed record TimesheetDay(
    DateOnly Date, DayStatus Status, string? ShiftCode, string? ShiftColor, string? PlannedStart, string? PlannedEnd,
    string? CheckIn, string? CheckOut, int PlannedMinutes, int WorkedMinutes, int LateMinutes, int OvertimeMinutes,
    AttendanceSource? Source, string? Note);

public sealed class TimesheetTotals
{
    public int PlannedDays { get; set; }
    public int PresentDays { get; set; }
    public int LateDays { get; set; }
    public int AbsentDays { get; set; }
    public int LeaveDays { get; set; }
    public int PlannedMinutes { get; set; }
    public int WorkedMinutes { get; set; }
    public int LateMinutes { get; set; }
    public int OvertimeMinutes { get; set; }
}

public sealed record TimesheetRow(Guid EmployeeId, string EmployeeNo, string EmployeeName, string? DepartmentName, List<TimesheetDay> Days, TimesheetTotals Totals);

public sealed record TimesheetDto(string Month, List<DateOnly> Days, DateOnly Today, List<TimesheetRow> Rows);

public sealed record MyAttendanceDto(TimesheetDay Today, bool CanCheckIn, bool CanCheckOut, TimesheetRow Row);

public sealed record PlanEmployee(Guid Id, string EmployeeNo, string Name, string? DepartmentName);

public sealed record PlanEntry(Guid EmployeeId, DateOnly Date, Guid? ShiftId);

public sealed record ShiftPlanDto(DateOnly From, DateOnly To, List<PlanEmployee> Employees, List<PlanEntry> Entries);

public sealed record ShiftPlanChange(Guid EmployeeId, DateOnly Date, Guid? ShiftId, bool UseDefault);

public sealed record UpdateShiftPlanRequest(List<ShiftPlanChange> Entries);

public sealed record UpdateRecordRequest(Guid EmployeeId, DateOnly Date, string? CheckIn, string? CheckOut, string? Note);

/// <summary>Shift planning (vardiya) and attendance (puantaj).</summary>
[ModuleService]
public sealed class AttendanceService(AppDbContext db)
{
    private const int MaxPlanDays = 31;
    private const string DefaultTimeZone = "Europe/Istanbul";

    private sealed record EmployeeRow(Guid Id, string EmployeeNo, string FirstName, string LastName, DateOnly HireDate, DateOnly? TerminationDate, string? Department);

    // ---------- shift plan ----------

    public async Task<ShiftPlanDto> PlanAsync(DateOnly from, DateOnly to, Guid? departmentId)
    {
        Guard.That(to >= from, "Bitiş başlangıçtan önce olamaz");
        Guard.That(to.DayNumber - from.DayNumber < MaxPlanDays, $"Aralık en fazla {MaxPlanDays} gün olabilir");
        var employees = await EmployeesAsync(from, to, departmentId);
        var ids = employees.Select(e => e.Id).ToList();
        var entries = await db.ShiftAssignments.AsNoTracking().Where(a => a.Date >= from && a.Date <= to && ids.Contains(a.EmployeeId))
            .OrderBy(a => a.Date).Select(a => new PlanEntry(a.EmployeeId, a.Date, a.ShiftId)).ToListAsync();
        return new ShiftPlanDto(from, to, employees.Select(e => new PlanEmployee(e.Id, e.EmployeeNo, $"{e.FirstName} {e.LastName}", e.Department)).ToList(), entries);
    }

    public async Task<int> UpdatePlanAsync(UpdateShiftPlanRequest body)
    {
        Guard.That(body.Entries.Count is > 0 and <= 1000, "1 ile 1000 arasında değişiklik gönderin");
        var employeeIds = body.Entries.Select(e => e.EmployeeId).Distinct().ToList();
        if (await db.Employees.CountAsync(e => employeeIds.Contains(e.Id)) != employeeIds.Count) throw new NotFoundAppException("Personel bulunamadı");
        var shiftIds = body.Entries.Where(e => !e.UseDefault && e.ShiftId != null).Select(e => e.ShiftId!.Value).Distinct().ToList();
        if (await db.Shifts.CountAsync(s => s.IsActive && shiftIds.Contains(s.Id)) != shiftIds.Count) throw new NotFoundAppException("Vardiya bulunamadı veya aktif değil");

        var dates = body.Entries.Select(e => e.Date).Distinct().ToList();
        var existing = await db.ShiftAssignments.Where(a => employeeIds.Contains(a.EmployeeId) && dates.Contains(a.Date)).ToListAsync();
        foreach (var change in body.Entries)
        {
            var row = existing.FirstOrDefault(a => a.EmployeeId == change.EmployeeId && a.Date == change.Date);
            if (change.UseDefault)
            {
                if (row != null) db.ShiftAssignments.Remove(row);
                continue;
            }
            if (row == null)
            {
                row = new ShiftAssignment { EmployeeId = change.EmployeeId, Date = change.Date };
                db.ShiftAssignments.Add(row);
                existing.Add(row);
            }
            row.ShiftId = change.ShiftId;
        }
        await db.SaveChangesAsync();
        return body.Entries.Count;
    }

    // ---------- timesheet ----------

    public async Task<TimesheetDto> TimesheetAsync(string month, Guid? departmentId)
    {
        var (from, to) = MonthRange(month);
        var employees = await EmployeesAsync(from, to, departmentId);
        var offset = await OffsetAsync();
        var days = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1)) days.Add(d);
        return new TimesheetDto(month, days, Today(offset), await RowsAsync(employees, from, to, offset));
    }

    public async Task<MyAttendanceDto> MineAsync(string? month)
    {
        var employee = await OwnAsync();
        var offset = await OffsetAsync();
        var today = Today(offset);
        var (from, to) = MonthRange(month ?? today.ToString("yyyy-MM"));
        var row = (await RowsAsync([employee], from, to, offset))[0];
        var todayRow = (await RowsAsync([employee], today, today, offset))[0];
        var record = await db.AttendanceRecords.AsNoTracking().FirstOrDefaultAsync(r => r.EmployeeId == employee.Id && r.Date == today);
        var open = await OpenRecordAsync(employee.Id, today);
        return new MyAttendanceDto(todayRow.Days[0], record?.CheckIn == null, open != null, row);
    }

    public async Task<MyAttendanceDto> CheckInAsync()
    {
        var employee = await OwnAsync();
        var today = Today(await OffsetAsync());
        var record = await db.AttendanceRecords.FirstOrDefaultAsync(r => r.EmployeeId == employee.Id && r.Date == today);
        Guard.State(record?.CheckIn == null, "Bugün zaten giriş yaptınız");
        if (record == null)
        {
            record = new AttendanceRecord { EmployeeId = employee.Id, Date = today };
            db.AttendanceRecords.Add(record);
        }
        record.CheckIn = DateTime.UtcNow;
        record.Source = AttendanceSource.SELF;
        await db.SaveChangesAsync();
        return await MineAsync(null);
    }

    public async Task<MyAttendanceDto> CheckOutAsync()
    {
        var employee = await OwnAsync();
        var open = await OpenRecordAsync(employee.Id, Today(await OffsetAsync()), tracked: true)
            ?? throw new ConflictAppException("Kapatılacak açık bir giriş yok");
        open.CheckOut = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return await MineAsync(null);
    }

    /// <summary>HR correction of one day; clearing the times and the note removes the record.</summary>
    public async Task<TimesheetDay> UpdateRecordAsync(UpdateRecordRequest body)
    {
        var e = await db.Employees.AsNoTracking().Where(x => x.Id == body.EmployeeId)
            .Select(x => new EmployeeRow(x.Id, x.EmployeeNo, x.FirstName, x.LastName, x.HireDate, x.TerminationDate, x.Department!.Name))
            .FirstOrDefaultAsync() ?? throw new NotFoundAppException("Personel bulunamadı");
        var checkIn = string.IsNullOrWhiteSpace(body.CheckIn) ? null : body.CheckIn.Trim();
        var checkOut = string.IsNullOrWhiteSpace(body.CheckOut) ? null : body.CheckOut.Trim();
        Guard.That((checkIn == null || Timesheet.IsClock(checkIn)) && (checkOut == null || Timesheet.IsClock(checkOut)), "Saatler SS:dd biçiminde olmalı");
        Guard.That(checkOut == null || checkIn != null, "Çıkış için giriş saati gerekir");
        var note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
        Guard.That(note == null || note.Length <= 500, "Not en fazla 500 karakter olabilir");
        var offset = await OffsetAsync();

        var record = await db.AttendanceRecords.FirstOrDefaultAsync(r => r.EmployeeId == e.Id && r.Date == body.Date);
        if (checkIn == null && note == null)
        {
            if (record != null) db.AttendanceRecords.Remove(record);
        }
        else
        {
            if (record == null)
            {
                record = new AttendanceRecord { EmployeeId = e.Id, Date = body.Date };
                db.AttendanceRecords.Add(record);
            }
            int? inMinutes = checkIn == null ? null : Timesheet.ParseClock(checkIn);
            int? outMinutes = checkOut == null ? null : Timesheet.ParseClock(checkOut);
            if (inMinutes != null && outMinutes != null && outMinutes <= inMinutes) outMinutes += Timesheet.DayMinutes;
            record.CheckIn = inMinutes == null ? null : Timesheet.InstantOf(body.Date, inMinutes.Value, offset);
            record.CheckOut = outMinutes == null ? null : Timesheet.InstantOf(body.Date, outMinutes.Value, offset);
            record.Note = note;
            record.Source = AttendanceSource.MANUAL;
            record.UpdatedById = db.User.UserId;
        }
        await db.SaveChangesAsync();
        return (await RowsAsync([e], body.Date, body.Date, offset))[0].Days[0];
    }

    // ---------- helpers ----------

    private async Task<List<TimesheetRow>> RowsAsync(List<EmployeeRow> employees, DateOnly from, DateOnly to, int offset)
    {
        var ids = employees.Select(e => e.Id).ToList();
        var plans = await db.ShiftAssignments.AsNoTracking().Include(a => a.Shift).Where(a => ids.Contains(a.EmployeeId) && a.Date >= from && a.Date <= to).ToListAsync();
        var records = await db.AttendanceRecords.AsNoTracking().Where(r => ids.Contains(r.EmployeeId) && r.Date >= from && r.Date <= to).ToListAsync();
        var holidays = (await db.Holidays.Where(h => h.Date >= from && h.Date <= to).Select(h => h.Date).ToListAsync()).ToHashSet();
        var leaves = await db.LeaveRequests.AsNoTracking()
            .Where(l => ids.Contains(l.EmployeeId) && l.Status == LeaveRequestStatus.APPROVED && l.StartDate <= to && l.EndDate >= from)
            .Select(l => new { l.EmployeeId, l.StartDate, l.EndDate }).ToListAsync();
        var planByKey = plans.ToDictionary(a => (a.EmployeeId, a.Date));
        var recordByKey = records.ToDictionary(r => (r.EmployeeId, r.Date));
        var now = DateTime.UtcNow;

        return employees.Select(e =>
        {
            var totals = new TimesheetTotals();
            var days = new List<TimesheetDay>();
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                var employed = date >= e.HireDate && (e.TerminationDate == null || date <= e.TerminationDate);
                var shift = employed ? ResolveShift(date, planByKey.GetValueOrDefault((e.Id, date))) : null;
                var record = recordByKey.GetValueOrDefault((e.Id, date));
                var onLeave = employed && leaves.Any(l => l.EmployeeId == e.Id && l.StartDate <= date && date <= l.EndDate);
                var holiday = employed && holidays.Contains(date);
                int? checkIn = record?.CheckIn is { } ci ? Timesheet.MinutesSince(ci, date, offset) : null;
                int? checkOut = record?.CheckOut is { } co ? Timesheet.MinutesSince(co, date, offset) : null;
                var result = Timesheet.Evaluate(shift, holiday, onLeave, checkIn, checkOut, Timesheet.MinutesSince(now, date, offset));

                if (result.PlannedMinutes > 0) totals.PlannedDays++;
                if (result.Status is DayStatus.PRESENT or DayStatus.LATE or DayStatus.INCOMPLETE) totals.PresentDays++;
                if (result.LateMinutes > 0) totals.LateDays++;
                if (result.Status == DayStatus.ABSENT) totals.AbsentDays++;
                if (result.Status == DayStatus.LEAVE) totals.LeaveDays++;
                totals.PlannedMinutes += result.PlannedMinutes;
                totals.WorkedMinutes += result.WorkedMinutes;
                totals.LateMinutes += result.LateMinutes;
                totals.OvertimeMinutes += result.OvertimeMinutes;

                var window = shift == null ? ((int, int)?)null : Timesheet.Window(shift);
                days.Add(new TimesheetDay(date, result.Status, shift?.Code, shift?.Color,
                    window == null ? null : Timesheet.FormatClock(window.Value.Item1), window == null ? null : Timesheet.FormatClock(window.Value.Item2),
                    checkIn == null ? null : Timesheet.FormatClock(checkIn.Value), checkOut == null ? null : Timesheet.FormatClock(checkOut.Value),
                    result.PlannedMinutes, result.WorkedMinutes, result.LateMinutes, result.OvertimeMinutes, record?.Source, record?.Note));
            }
            return new TimesheetRow(e.Id, e.EmployeeNo, $"{e.FirstName} {e.LastName}", e.Department, days, totals);
        }).ToList();
    }

    /// <summary>An explicit plan entry (null shift = day off), else the default weekday schedule.</summary>
    private static ShiftTimes? ResolveShift(DateOnly date, ShiftAssignment? entry)
    {
        if (entry != null) return entry.Shift is { } s ? new ShiftTimes(s.Code, s.StartTime, s.EndTime, s.BreakMinutes, s.Color) : null;
        return WorkingDays.IsWeekend(date) ? null : Timesheet.DefaultShift;
    }

    private Task<List<EmployeeRow>> EmployeesAsync(DateOnly from, DateOnly to, Guid? departmentId)
    {
        var q = db.Employees.AsNoTracking().Where(e => e.HireDate <= to && (e.TerminationDate == null || e.TerminationDate >= from));
        if (departmentId != null) q = q.Where(e => e.DepartmentId == departmentId);
        return q.OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new EmployeeRow(e.Id, e.EmployeeNo, e.FirstName, e.LastName, e.HireDate, e.TerminationDate, e.Department!.Name)).ToListAsync();
    }

    private async Task<EmployeeRow> OwnAsync()
    {
        var userId = db.User.RequireUserId();
        return await db.Employees.AsNoTracking().Where(e => e.UserId == userId)
            .Select(e => new EmployeeRow(e.Id, e.EmployeeNo, e.FirstName, e.LastName, e.HireDate, e.TerminationDate, e.Department!.Name))
            .FirstOrDefaultAsync() ?? throw new NotFoundAppException("Hesabınıza bağlı bir personel kaydı yok");
    }

    /// <summary>Latest check-in without a check-out from today or yesterday (night shifts).</summary>
    private Task<AttendanceRecord?> OpenRecordAsync(Guid employeeId, DateOnly today, bool tracked = false)
    {
        var yesterday = today.AddDays(-1);
        var q = db.AttendanceRecords.Where(r => r.EmployeeId == employeeId && r.CheckIn != null && r.CheckOut == null && r.Date >= yesterday && r.Date <= today);
        if (!tracked) q = q.AsNoTracking();
        return q.OrderByDescending(r => r.Date).FirstOrDefaultAsync();
    }

    public async Task<int> OffsetAsync()
    {
        var setting = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "general");
        var zone = setting?.Value.RootElement.TryGetProperty("timezone", out var tz) == true && tz.ValueKind == System.Text.Json.JsonValueKind.String
            ? tz.GetString()! : DefaultTimeZone;
        TimeZoneInfo info;
        try { info = TimeZoneInfo.FindSystemTimeZoneById(zone); }
        catch (Exception) { info = TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZone); }
        return (int)info.GetUtcOffset(DateTime.UtcNow).TotalMinutes;
    }

    public static DateOnly Today(int offset) => DateOnly.FromDateTime(DateTime.UtcNow.AddMinutes(offset));

    private static (DateOnly From, DateOnly To) MonthRange(string month)
    {
        Guard.That(DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", out var from), "Ay YYYY-AA biçiminde olmalı");
        return (from, from.AddMonths(1).AddDays(-1));
    }
}
